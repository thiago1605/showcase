"use client";

import { useEffect } from "react";
import {
  HttpTransportType,
  HubConnectionBuilder,
  HubConnectionState,
  LogLevel,
} from "@microsoft/signalr";
import { useQueryClient } from "@tanstack/react-query";

const API_BASE_URL =
  process.env.NEXT_PUBLIC_API_BASE_URL || "http://localhost:5000";
const HUB_URL = `${API_BASE_URL}/hubs/notifications`;
const TOKEN_KEY = "fellow_access_token";

/**
 * SignalR client pra push real-time de notificações in-app — Sprint 2 Fase 2.
 *
 * Como funciona:
 *  - Conecta no hub `/hubs/notifications` com JWT (acesso token do sessionStorage).
 *  - Backend coloca a connection no group `seller-{id}` baseado no claim.
 *  - Quando o <c>NotificationOutboxProcessor</c> materializa uma notification,
 *    broadcast pro group → cliente recebe evento "Notification" com
 *    type="notification.created" → invalidamos a query do TanStack →
 *    badge + dropdown refetcham instantaneamente.
 *  - Polling de 30s continua rodando como fallback (graceful degradation se
 *    SignalR cair ou WebSocket for bloqueado por proxy/firewall).
 *
 * Reconexão automática via withAutomaticReconnect (defaults — exponential
 * backoff até 30s, depois para). React Query mantém cache, então durante
 * desconexão temporária o seller vê dados (stale-while-revalidate).
 *
 * IMPORTANT — React 19 strict mode:
 *   useEffect roda 2x em dev (StrictMode propositalmente desmonta + remonta).
 *   Se chamarmos `connection.stop()` no cleanup enquanto `connection.start()`
 *   ainda está em negociação, a Promise do start falha com
 *   "The connection was stopped during negotiation."
 *   Fix: encadeamos o stop() DEPOIS do start() resolver (sucesso OU erro)
 *   via `startPromise.finally(...)`. Em prod sem strict mode é o mesmo —
 *   só garante ordem.
 */
export function useNotificationsRealtime() {
  const queryClient = useQueryClient();

  useEffect(() => {
    const token =
      typeof window !== "undefined" ? sessionStorage.getItem(TOKEN_KEY) : null;
    if (!token) return;

    let cancelled = false;
    const connection = new HubConnectionBuilder()
      .withUrl(HUB_URL, {
        accessTokenFactory: () =>
          sessionStorage.getItem(TOKEN_KEY) ?? "",
        transport: HttpTransportType.LongPolling,
      })
      // Retry policy explícito. `withAutomaticReconnect()` default tenta em
      // 0, 2, 10, 30s — bom pra glitches de rede transientes, ruim pra 401
      // (JWT expirado): cada retry gera negotiate + long-poll + preflight +
      // cleanup. Em 11min de sessão idle, isso vira centenas de requests
      // poluindo o Network panel. E nada disso resolve — o token só
      // refresca quando o api client faz uma chamada autenticada.
      //
      // Estratégia atual:
      //   - 401 → para de tentar. Polling de 30s do TanStack já cobre.
      //     Quando o api client refresca o token (na próxima call autenticada),
      //     a página reabre o SignalR no próximo mount/refresh.
      //   - Outros erros → 3 tentativas com backoff exponencial (2s, 10s, 30s).
      //     Após isso, desiste — polling segue sendo o caminho.
      .withAutomaticReconnect({
        nextRetryDelayInMilliseconds: (retryContext) => {
          const reason = retryContext.retryReason;
          const msg = reason instanceof Error ? reason.message : String(reason ?? "");
          // 401 = token expirado. Não adianta retry — precisa de refresh
          // que só rola via api client em outra call.
          if (msg.includes("401")) return null;
          // Cap de 3 tentativas
          if (retryContext.previousRetryCount >= 3) return null;
          // Backoff exponencial: 2s, 10s, 30s
          const delays = [2000, 10000, 30000];
          return delays[retryContext.previousRetryCount] ?? null;
        },
      })
      // Critical-only — silencia os Error logs do SignalR client (401 esperado
      // por JWT expirar é tratado gracefully no nosso `onclose`). Logs do
      // hub mesmo crítico (schema mismatch, etc) ainda aparecem.
      .configureLogging(LogLevel.Critical)
      .build();

    connection.on(
      "Notification",
      (msg: { type?: string; data?: unknown; timestamp?: string }) => {
        if (cancelled) return;
        if (msg?.type === "notification.created") {
          queryClient.invalidateQueries({ queryKey: ["notifications"] });
        }
      },
    );

    // 401 durante a sessão = JWT expirou. O api client tem auto-refresh, mas
    // o SignalR não passa por ele — o long-poll falha com 401 e fecha.
    // Polling 30s do TanStack vai acionar o refresh natural na próxima call.
    // Silenciamos o erro pra não poluir o console — não é erro real do usuário.
    connection.onclose((err) => {
      if (cancelled) return;
      const message = err instanceof Error ? err.message : String(err ?? "");
      if (message.includes("401")) {
        // Esperado quando o token expira. Próxima call HTTP do api client
        // refresca o token, e o SignalR reconecta via withAutomaticReconnect.
        if (process.env.NODE_ENV !== "production") {
          console.info(
            "[SignalR] connection closed (401 — token expirado, polling vai recuperar)",
          );
        }
        return;
      }
      // Outros disconnects (network down, backend reiniciou) — só info,
      // reconexão automática vai tentar.
      if (process.env.NODE_ENV !== "production") {
        console.info("[SignalR] connection closed:", message);
      }
    });

    // Mantém referência da start promise — o cleanup encadeia o stop NESSE
    // chain, garantindo que stop só rode após a negociação resolver.
    const startPromise = connection
      .start()
      .then(() => {
        if (process.env.NODE_ENV !== "production") {
          console.info("[SignalR] notifications hub connected");
        }
      })
      .catch((err) => {
        // Se foi cancelado durante o start (strict mode double-effect ou
        // umount rápido), engole — não é erro real do usuário.
        if (cancelled) return;
        console.warn("[SignalR] notifications hub failed to connect:", err);
      });

    return () => {
      cancelled = true;
      // Stop SÓ depois do start resolver. Sem isso, em strict mode o stop
      // dispara durante negociação → "stopped during negotiation".
      startPromise.finally(() => {
        if (connection.state !== HubConnectionState.Disconnected) {
          connection.stop().catch(() => {
            /* já parou ou nunca iniciou — silencia */
          });
        }
      });
    };
  }, [queryClient]);
}
