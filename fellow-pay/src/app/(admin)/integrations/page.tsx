"use client";
/**
 * Integrações do producer (Webhooks pro produtor).
 *
 * Diferença vs /webhooks (dev/admin):
 *   - /webhooks lista webhooks tenant-wide (dev cadastra, recebe TUDO do tenant).
 *   - /integrations lista webhooks DO PRODUCER (filtrados por seller_id do JWT).
 *
 * Foco: integrar venda do producer com ferramentas de marketing automation
 * (RD Station, ActiveCampaign, Mailchimp). O payload enviado inclui customer
 * (email/nome/CPF), produto, affiliate e UTM — tudo o que essas ferramentas
 * precisam pra criar lead, mover funil, etc.
 *
 * UI menor que /webhooks (sem dead-letters, retry-all, etc). Focada no
 * essencial: ver, criar, testar, rotacionar, remover.
 */
import React, { useState } from "react";
import { useScrollLock } from "@/hooks/useScrollLock";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import {
  producerWebhooksService,
  ProducerWebhookTestResult,
  PRODUCER_WEBHOOK_EVENTS,
} from "@/services/producer-webhooks.service";
import { CardListSkeleton } from "@/components/ui/Skeleton";
import { ConfirmModal } from "@/components/ui/ConfirmModal";
import Input from "@/components/form/input/InputField";
import { Illustration } from "@/components/ui/Illustration";
import { PageHeader, PageHeaderButton } from "@/components/ui/PageHeader";
import type { WebhookEndpoint } from "@/types";

const TEST_EVENT_OPTIONS: readonly string[] = [
  "webhook.test",
  ...PRODUCER_WEBHOOK_EVENTS,
];

/**
 * Exemplo de payload mostrado inline no form pra ajudar producer a entender
 * o que vai receber. Renderizado num <pre> dentro de <details collapsible>.
 */
const SAMPLE_PAYLOAD = JSON.stringify(
  {
    event: "transaction.captured",
    data: {
      id: "11111111-2222-3333-4444-555555555555",
      provider_id: "pi_3O2x...",
      status: "CAPTURED",
      amount: 197.0,
      type: "CREDIT_CARD",
      updated_at: "2026-05-26T20:00:00Z",
      external_reference_id: "product:aaaa1111-...",
      customer: {
        email: "cliente@dominio.com",
        name: "Maria Silva",
        document: "12345678900",
      },
      product: {
        id: "aaaa1111-bbbb-2222-cccc-3333dddd4444",
        name: "Curso de Investimentos",
        slug: "curso-investimentos",
      },
      affiliate: {
        id: "ffff9999-...",
        name: "Influencer X",
      },
      utm: {
        source: "instagram",
        medium: "stories",
        campaign: "blackfriday-2026",
        content: null,
        term: null,
      },
    },
  },
  null,
  2,
);

export default function IntegrationsPage() {
  const queryClient = useQueryClient();
  const [showForm, setShowForm] = useState(false);
  const [formUrl, setFormUrl] = useState("");
  const [formSecret, setFormSecret] = useState("");
  const [formEvents, setFormEvents] = useState<string[]>([
    "transaction.captured",
  ]);
  const [formLoading, setFormLoading] = useState(false);
  const [formError, setFormError] = useState("");

  // Modal de teste — escolha de evento + último resultado por endpoint.
  const [testingId, setTestingId] = useState<string | null>(null);
  const [testEventType, setTestEventType] = useState<string>("webhook.test");
  const [testRunning, setTestRunning] = useState(false);
  const [testResult, setTestResult] = useState<ProducerWebhookTestResult | null>(null);

  // Confirmações destrutivas — código de verificação obrigatório.
  const [pendingAction, setPendingAction] = useState<{
    title: string;
    message: string;
    confirmLabel: string;
    run: () => Promise<void>;
  } | null>(null);
  const [pendingRunning, setPendingRunning] = useState(false);

  // Modal de rotação — em 2 etapas: confirmação → resultado com secret novo.
  const [rotatingId, setRotatingId] = useState<string | null>(null);
  const [rotateRunning, setRotateRunning] = useState(false);
  const [rotateError, setRotateError] = useState<string | null>(null);
  const [rotateNewSecret, setRotateNewSecret] = useState<string | null>(null);
  const [rotateCopied, setRotateCopied] = useState(false);

  // Filtro de status — Todas / Ativas / Desativadas. Chip group acima da
  // lista. Empty state quando filtro retorna 0 difere de "nenhuma cadastrada".
  const [statusFilter, setStatusFilter] = useState<"ALL" | "ACTIVE" | "DISABLED">("ALL");

  // Trava scroll quando qualquer um dos 3 modais (criar / testar / rotacionar)
  // estiver aberto. ConfirmModal de delete já se auto-trava.
  useScrollLock(showForm || testingId !== null || rotatingId !== null);

  const { data: endpoints = [], isLoading } = useQuery<WebhookEndpoint[]>({
    queryKey: ["producer-webhooks", "list"],
    queryFn: () => producerWebhooksService.listMyWebhooks(),
  });
  const invalidate = () =>
    queryClient.invalidateQueries({ queryKey: ["producer-webhooks"] });

  const openTest = (id: string) => {
    setTestingId(id);
    setTestEventType("webhook.test");
    setTestResult(null);
  };

  const runTest = async () => {
    if (!testingId) return;
    setTestRunning(true);
    setTestResult(null);
    try {
      const result = await producerWebhooksService.testMyWebhook(
        testingId,
        testEventType,
      );
      setTestResult(result);
    } catch (err) {
      setTestResult({
        success: false,
        statusCode: 0,
        latencyMs: 0,
        responseBody: null,
        error: err instanceof Error ? err.message : "Erro inesperado.",
      });
    }
    setTestRunning(false);
  };

  const openRotate = (id: string) => {
    setRotatingId(id);
    setRotateError(null);
    setRotateNewSecret(null);
    setRotateCopied(false);
  };

  const closeRotate = () => {
    setRotatingId(null);
    setRotateError(null);
    setRotateNewSecret(null);
    setRotateCopied(false);
  };

  const runRotate = async () => {
    if (!rotatingId) return;
    setRotateRunning(true);
    setRotateError(null);
    try {
      const { secret } = await producerWebhooksService.rotateSecret(rotatingId);
      setRotateNewSecret(secret);
    } catch (err) {
      setRotateError(err instanceof Error ? err.message : "Erro ao rotacionar.");
    }
    setRotateRunning(false);
  };

  const copySecret = async () => {
    if (!rotateNewSecret) return;
    try {
      await navigator.clipboard.writeText(rotateNewSecret);
      setRotateCopied(true);
      setTimeout(() => setRotateCopied(false), 2000);
    } catch {
      /* silently fail */
    }
  };

  // Gera um segredo aleatório (32 bytes hex).
  const generateSecret = () => {
    const bytes = new Uint8Array(32);
    crypto.getRandomValues(bytes);
    setFormSecret(
      Array.from(bytes, (b) => b.toString(16).padStart(2, "0")).join(""),
    );
  };

  const handleCreate = async (e: React.FormEvent) => {
    e.preventDefault();
    setFormError("");
    if (formEvents.length === 0) {
      setFormError("Selecione pelo menos um evento.");
      return;
    }
    if (!formSecret.trim()) {
      setFormError("Informe ou gere um segredo HMAC.");
      return;
    }
    setFormLoading(true);
    try {
      await producerWebhooksService.createMyWebhook({
        url: formUrl,
        secret: formSecret.trim(),
        events: formEvents,
      });
      setShowForm(false);
      setFormUrl("");
      setFormSecret("");
      setFormEvents(["transaction.captured"]);
      invalidate();
    } catch (err) {
      setFormError(
        err instanceof Error ? err.message : "Erro ao criar webhook.",
      );
    }
    setFormLoading(false);
  };

  const handleToggle = (id: string, enabled: boolean, url: string) => {
    if (!enabled) {
      void producerWebhooksService
        .toggleMyWebhook(id, true)
        .then(invalidate)
        .catch(() => {});
      return;
    }
    setPendingAction({
      title: "Desativar webhook",
      message: `Desativar o endpoint ${url}? Eventos pararão de ser entregues até reativar.`,
      confirmLabel: "Desativar",
      run: async () => {
        await producerWebhooksService.toggleMyWebhook(id, false);
        invalidate();
      },
    });
  };

  const handleDelete = (id: string, url: string) => {
    setPendingAction({
      title: "Remover integração",
      message: `Remover permanentemente ${url}? Esta ação não pode ser desfeita.`,
      confirmLabel: "Remover",
      run: async () => {
        await producerWebhooksService.deleteMyWebhook(id);
        invalidate();
      },
    });
  };

  const runPending = async () => {
    if (!pendingAction) return;
    setPendingRunning(true);
    try {
      await pendingAction.run();
      setPendingAction(null);
    } catch {
      /* silently fail */
    }
    setPendingRunning(false);
  };

  const toggleEvent = (event: string) => {
    setFormEvents((prev) =>
      prev.includes(event) ? prev.filter((e) => e !== event) : [...prev, event],
    );
  };

  if (isLoading) {
    return <CardListSkeleton count={3} ariaLabel="Carregando integrações" />;
  }

  return (
    <div className="space-y-6">
      <PageHeader
        size="hero"
        title="Integrações"
        subtitle="Receba notificações em tempo real das suas vendas. Conecte com RD Station, ActiveCampaign, Mailchimp ou qualquer ferramenta que aceite webhook HTTP. O segredo HMAC só aparece no cadastro ou na rotação — anote na hora."
        actions={
          <PageHeaderButton onClick={() => setShowForm(true)}>
            + Nova integração
          </PageHeaderButton>
        }
        decorIcon={
          <svg
            viewBox="0 0 24 24"
            fill="none"
            stroke="currentColor"
            strokeWidth="1.2"
            strokeLinecap="round"
            strokeLinejoin="round"
            aria-hidden="true"
          >
            <path d="M7 17a5 5 0 0 1-2-9.5A5.5 5.5 0 0 1 16 6a4 4 0 0 1 3.5 6" />
            <path d="M12 12v9" />
            <path d="m8 17 4 4 4-4" />
          </svg>
        }
      />

      {endpoints.length === 0 ? (
        // Empty state inicial — sem integrações cadastradas. Ilustração + 2 linhas
        // + CTA inline, mesmo padrão de /split-rules /products etc.
        <div className="rounded-2xl border border-gray-200 bg-white dark:border-gray-800 dark:bg-white/[0.03] p-10 text-center">
          <Illustration
            name="integrations-empty"
            size="xl"
            className="mx-auto mb-4 max-w-[16rem]"
          />
          <p className="text-sm font-medium text-gray-900 dark:text-white">
            Nenhuma integração configurada
          </p>
          <p className="text-xs text-gray-500 dark:text-gray-400 mt-1 max-w-md mx-auto">
            Adicione uma URL para receber notificações por venda. Útil para
            conectar com ferramentas de marketing automation, CRMs ou seu
            próprio sistema.
          </p>
          <button
            type="button"
            onClick={() => setShowForm(true)}
            className="inline-flex items-center mt-4 h-9 rounded-lg bg-brand-500 hover:bg-brand-600 px-4 text-sm font-semibold text-white transition-colors"
          >
            + Criar primeira integração
          </button>
        </div>
      ) : (() => {
        const filteredEndpoints = endpoints.filter((ep) => {
          if (statusFilter === "ACTIVE") return ep.enabled;
          if (statusFilter === "DISABLED") return !ep.enabled;
          return true;
        });
        const FILTERS: { value: typeof statusFilter; label: string }[] = [
          { value: "ALL", label: `Todas (${endpoints.length})` },
          { value: "ACTIVE", label: `Ativas (${endpoints.filter((e) => e.enabled).length})` },
          { value: "DISABLED", label: `Desativadas (${endpoints.filter((e) => !e.enabled).length})` },
        ];
        return (
          <div className="space-y-3">
            {/* Filtro de status — pill chips iguais ao PeriodPicker. Contagem
                inline pra dar feedback rápido de quantas tem em cada bucket. */}
            <div className="inline-flex p-0.5 bg-gray-100 dark:bg-gray-800 rounded-lg">
              {FILTERS.map((f) => (
                <button
                  key={f.value}
                  type="button"
                  onClick={() => setStatusFilter(f.value)}
                  aria-pressed={statusFilter === f.value}
                  className={`h-7 px-3 text-xs font-semibold rounded-md transition-colors tabular-nums ${
                    statusFilter === f.value
                      ? "bg-white dark:bg-gray-900 text-gray-900 dark:text-white shadow-sm"
                      : "text-gray-500 dark:text-gray-400 hover:text-gray-900 dark:hover:text-white"
                  }`}
                >
                  {f.label}
                </button>
              ))}
            </div>

            {filteredEndpoints.length === 0 ? (
              <div className="rounded-2xl border border-gray-200 bg-white dark:border-gray-800 dark:bg-white/[0.03] p-8 text-center">
                <p className="text-sm text-gray-500 dark:text-gray-400">
                  Nenhuma integração {statusFilter === "ACTIVE" ? "ativa" : "desativada"} no momento.
                </p>
              </div>
            ) : (
        <ul className="rounded-2xl border border-gray-200 bg-white dark:border-gray-800 dark:bg-white/[0.03] divide-y divide-gray-100 dark:divide-gray-800 overflow-hidden">
          {filteredEndpoints.map((ep) => (
            <li
              key={ep.id}
              className="flex items-center justify-between gap-3 px-5 py-4"
            >
              <div className="min-w-0 flex-1">
                <p className="text-sm font-medium text-gray-900 dark:text-white font-mono truncate">
                  {ep.url}
                </p>
                <div className="flex flex-wrap gap-1 mt-1.5">
                  {ep.events.map((ev) => (
                    <span
                      key={ev}
                      className="inline-flex rounded-md bg-gray-100 px-1.5 py-0.5 text-[10px] font-medium text-gray-600 dark:bg-gray-800 dark:text-gray-400"
                    >
                      {ev}
                    </span>
                  ))}
                </div>
              </div>
              <div className="flex items-center gap-1.5 shrink-0">
                <button
                  type="button"
                  onClick={() => openTest(ep.id)}
                  className="h-8 rounded-lg border border-gray-200 dark:border-gray-700 bg-white dark:bg-gray-900 px-3 text-xs font-medium text-gray-700 dark:text-gray-300 hover:bg-gray-50 dark:hover:bg-gray-800 transition-colors"
                  title="Enviar evento sintético para este endpoint"
                >
                  Testar
                </button>
                <button
                  type="button"
                  onClick={() => openRotate(ep.id)}
                  className="h-8 rounded-lg border border-gray-200 dark:border-gray-700 bg-white dark:bg-gray-900 px-3 text-xs font-medium text-gray-700 dark:text-gray-300 hover:bg-gray-50 dark:hover:bg-gray-800 transition-colors"
                  title="Gerar um novo segredo HMAC (substitui o atual)"
                >
                  Rotacionar
                </button>
                <button
                  onClick={() => handleToggle(ep.id, ep.enabled, ep.url)}
                  className={`relative inline-flex h-5 w-9 shrink-0 items-center rounded-full transition-colors ${
                    ep.enabled ? "bg-brand-500" : "bg-gray-300 dark:bg-gray-700"
                  }`}
                  aria-label={ep.enabled ? "Desativar integração" : "Ativar integração"}
                >
                  <span
                    className={`inline-block h-3.5 w-3.5 rounded-full bg-white transition-transform ${
                      ep.enabled ? "translate-x-4.5" : "translate-x-0.5"
                    }`}
                  />
                </button>
                <button
                  type="button"
                  onClick={() => handleDelete(ep.id, ep.url)}
                  aria-label={`Remover integração ${ep.url}`}
                  title="Remover"
                  className="inline-flex items-center justify-center h-8 w-8 rounded-lg text-gray-500 hover:bg-error-50 hover:text-error-600 dark:text-gray-400 dark:hover:bg-error-500/10 dark:hover:text-error-400 transition-colors"
                >
                  <svg
                    width="16"
                    height="16"
                    viewBox="0 0 16 16"
                    fill="none"
                    aria-hidden="true"
                  >
                    <path
                      d="M6 3V2a1 1 0 0 1 1-1h2a1 1 0 0 1 1 1v1m-7 0h10m-9 0v10a1 1 0 0 0 1 1h6a1 1 0 0 0 1-1V3M7 6v6M9 6v6"
                      stroke="currentColor"
                      strokeWidth="1.4"
                      strokeLinecap="round"
                      strokeLinejoin="round"
                    />
                  </svg>
                </button>
              </div>
            </li>
          ))}
        </ul>
            )}
          </div>
        );
      })()}

      {/* Documentação inline — HMAC + retry policy. <details> colapsável
          (fechado por default) — sempre visível ocupava muito espaço pra
          users que já leram. Aberto on demand quando precisar consultar. */}
      <details className="group rounded-2xl border border-gray-200 bg-white dark:border-gray-800 dark:bg-white/[0.03] overflow-hidden">
        <summary className="flex items-center justify-between gap-3 px-5 py-3.5 cursor-pointer list-none hover:bg-gray-50/50 dark:hover:bg-white/[0.02] transition-colors">
          <div className="flex items-center gap-2 text-sm font-semibold text-gray-900 dark:text-white">
            <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.5" strokeLinecap="round" strokeLinejoin="round" className="text-gray-400" aria-hidden="true">
              <circle cx="12" cy="12" r="10" />
              <line x1="12" y1="16" x2="12" y2="12" />
              <line x1="12" y1="8" x2="12.01" y2="8" />
            </svg>
            Como funciona
            <span className="text-xs font-normal text-gray-500 dark:text-gray-400">· HMAC · Timeout · Retry · Idempotência</span>
          </div>
          <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true" className="shrink-0 text-gray-400 transition-transform group-open:rotate-180">
            <polyline points="6 9 12 15 18 9" />
          </svg>
        </summary>
        <div className="px-5 pb-5 pt-1 border-t border-gray-100 dark:border-gray-800/50">
        <ul className="space-y-2 text-xs text-gray-600 dark:text-gray-400 mt-3">
          <li>
            <strong className="text-gray-900 dark:text-gray-200">Assinatura HMAC:</strong>{" "}
            cada webhook é enviado com header{" "}
            <code className="font-mono text-[11px] bg-gray-100 dark:bg-gray-800 px-1 rounded">
              X-Webhook-Signature
            </code>{" "}
            contendo HMAC-SHA256 do corpo bruto (string JSON) usando seu segredo.
            Valide no servidor antes de processar.
          </li>
          <li>
            <strong className="text-gray-900 dark:text-gray-200">Timeout:</strong> 5
            segundos. Responda{" "}
            <code className="font-mono text-[11px] bg-gray-100 dark:bg-gray-800 px-1 rounded">
              200 OK
            </code>{" "}
            o mais rápido possível — processe o payload em background no seu lado.
          </li>
          <li>
            <strong className="text-gray-900 dark:text-gray-200">Retry:</strong>{" "}
            backoff exponencial até 5 tentativas em caso de falha (HTTP {">"}= 400 ou
            timeout). Depois disso, o evento vai para dead-letter (visível para o admin).
          </li>
          <li>
            <strong className="text-gray-900 dark:text-gray-200">Idempotência:</strong>{" "}
            o mesmo evento pode chegar mais de uma vez (em caso de retry). Use{" "}
            <code className="font-mono text-[11px] bg-gray-100 dark:bg-gray-800 px-1 rounded">
              data.id
            </code>{" "}
            como chave de idempotência no seu lado.
          </li>
        </ul>

        <details className="mt-4">
          <summary className="text-xs font-medium text-brand-600 hover:text-brand-700 dark:text-brand-400 cursor-pointer">
            Ver exemplo de payload (transaction.captured)
          </summary>
          <pre className="mt-2 text-[11px] text-gray-700 dark:text-gray-300 bg-gray-50 dark:bg-gray-900 rounded-lg p-3 overflow-x-auto whitespace-pre">
            {SAMPLE_PAYLOAD}
          </pre>
        </details>
        </div>
      </details>

      {showForm && (
        <div className="fixed inset-0 z-[100000] flex items-center justify-center bg-gray-400/5 backdrop-blur-[3px] modal-backdrop-in">
          <div className="w-full max-w-lg rounded-xl bg-white p-6 dark:bg-gray-900 shadow-xl max-h-[85vh] overflow-y-auto modal-content-in">
            <h3 className="text-lg font-semibold text-gray-900 dark:text-white mb-2">
              Nova Integração
            </h3>
            <p className="text-xs text-gray-500 dark:text-gray-400 mb-4">
              Vamos enviar um <code className="font-mono">POST</code> sintético
              assinado para a URL antes de salvar. Seu endpoint precisa responder{" "}
              <strong>HTTP 200</strong> em até 5 segundos para ser aceito.
            </p>
            {formError && (
              <div className="mb-4 p-3 rounded-lg bg-error-50 dark:bg-error-500/10 text-error-700 dark:text-error-400 text-sm whitespace-pre-line">
                {formError}
              </div>
            )}
            <form onSubmit={handleCreate} className="space-y-4">
              <Input
                label="URL do Endpoint"
                type="url"
                value={formUrl}
                onChange={(e) => setFormUrl(e.target.value)}
                placeholder="https://seu-servidor.com/webhook"
                required
              />
              <Input
                label="Segredo (HMAC)"
                type="text"
                value={formSecret}
                onChange={(e) => setFormSecret(e.target.value)}
                placeholder="Cole ou gere um segredo (mín. 16 caracteres)"
                minLength={16}
                required
                rightSlot={
                  <button
                    type="button"
                    onClick={generateSecret}
                    className="text-xs font-medium text-brand-600 hover:text-brand-700 dark:text-brand-400 dark:hover:text-brand-300"
                  >
                    Gerar
                  </button>
                }
                className="font-mono"
                hint="Use este segredo para validar a assinatura HMAC dos payloads. Anote agora — o backend só guarda criptografado."
              />
              <div>
                <label className="block text-sm font-medium text-gray-700 dark:text-gray-300 mb-2">
                  Eventos
                </label>
                <div className="grid grid-cols-1 gap-2 max-h-48 overflow-y-auto">
                  {PRODUCER_WEBHOOK_EVENTS.map((event) => (
                    <label
                      key={event}
                      className="flex items-center gap-2 text-sm text-gray-700 dark:text-gray-300 cursor-pointer"
                    >
                      <input
                        type="checkbox"
                        checked={formEvents.includes(event)}
                        onChange={() => toggleEvent(event)}
                        className="rounded border-gray-300 text-brand-500 focus:ring-brand-500"
                      />
                      {event}
                    </label>
                  ))}
                </div>
              </div>
              <div className="flex justify-end gap-3 pt-2">
                <button
                  type="button"
                  onClick={() => setShowForm(false)}
                  className="rounded-lg px-4 py-2 text-sm font-medium text-gray-600 hover:bg-gray-100 dark:text-gray-400 dark:hover:bg-gray-800"
                >
                  Cancelar
                </button>
                <button
                  type="submit"
                  disabled={formLoading}
                  className="rounded-lg bg-brand-500 px-4 py-2 text-sm font-medium text-white hover:bg-brand-600 disabled:opacity-50"
                >
                  {formLoading ? "Verificando..." : "Criar Integração"}
                </button>
              </div>
            </form>
          </div>
        </div>
      )}

      {testingId && (
        <div className="fixed inset-0 z-[100000] flex items-center justify-center bg-gray-400/5 backdrop-blur-[3px] modal-backdrop-in">
          <div className="w-full max-w-lg rounded-xl bg-white p-6 dark:bg-gray-900 shadow-xl max-h-[85vh] overflow-y-auto modal-content-in">
            <h3 className="text-lg font-semibold text-gray-900 dark:text-white mb-2">
              Enviar evento de teste
            </h3>
            <p className="text-xs text-gray-500 dark:text-gray-400 mb-4">
              Vamos disparar um <code className="font-mono">POST</code> com
              payload sintético assinado. Útil para validar URL, certificado TLS e
              verificação HMAC do seu lado.
            </p>

            <div className="space-y-3">
              <div>
                <label className="block text-sm font-medium text-gray-700 dark:text-gray-300 mb-1">
                  Tipo de evento
                </label>
                <select
                  value={testEventType}
                  onChange={(e) => setTestEventType(e.target.value)}
                  disabled={testRunning}
                  className="w-full rounded-lg border border-gray-200 px-4 py-2.5 text-sm dark:border-gray-700 dark:bg-gray-800 dark:text-white focus:border-brand-500 focus:outline-none disabled:opacity-50"
                >
                  {TEST_EVENT_OPTIONS.map((ev) => (
                    <option key={ev} value={ev}>
                      {ev}
                    </option>
                  ))}
                </select>
              </div>

              {testResult && (
                <div
                  className={`rounded-lg border p-3 ${
                    testResult.success
                      ? "border-success-200 bg-success-50 dark:border-success-500/30 dark:bg-success-500/10"
                      : "border-error-200 bg-error-50 dark:border-error-500/30 dark:bg-error-500/10"
                  }`}
                >
                  <div className="flex items-center justify-between">
                    <span
                      className={`text-sm font-semibold ${
                        testResult.success
                          ? "text-success-700 dark:text-success-300"
                          : "text-error-700 dark:text-error-300"
                      }`}
                    >
                      {testResult.success
                        ? `✓ HTTP ${testResult.statusCode}`
                        : testResult.statusCode > 0
                          ? `✗ HTTP ${testResult.statusCode}`
                          : "✗ Falhou"}
                    </span>
                    <span className="text-xs text-gray-500 dark:text-gray-400">
                      {testResult.latencyMs}ms
                    </span>
                  </div>
                  {testResult.error && (
                    <p className="text-xs text-error-700 dark:text-error-300 mt-1.5">
                      {testResult.error}
                    </p>
                  )}
                  {testResult.responseBody && (
                    <details className="mt-2">
                      <summary className="text-xs text-gray-600 dark:text-gray-400 cursor-pointer">
                        Ver corpo da resposta
                      </summary>
                      <pre className="mt-1 text-[11px] text-gray-700 dark:text-gray-300 bg-white dark:bg-gray-900 rounded p-2 overflow-x-auto whitespace-pre-wrap break-all">
                        {testResult.responseBody}
                      </pre>
                    </details>
                  )}
                </div>
              )}
            </div>

            <div className="flex justify-end gap-3 pt-4">
              <button
                type="button"
                onClick={() => {
                  setTestingId(null);
                  setTestResult(null);
                }}
                disabled={testRunning}
                className="rounded-lg px-4 py-2 text-sm font-medium text-gray-600 hover:bg-gray-100 dark:text-gray-400 dark:hover:bg-gray-800 disabled:opacity-50"
              >
                Fechar
              </button>
              <button
                type="button"
                onClick={runTest}
                disabled={testRunning}
                className="rounded-lg bg-brand-500 px-4 py-2 text-sm font-medium text-white hover:bg-brand-600 disabled:opacity-50"
              >
                {testRunning ? "Enviando..." : "Enviar"}
              </button>
            </div>
          </div>
        </div>
      )}

      {rotatingId && (
        <div className="fixed inset-0 z-[100000] flex items-center justify-center bg-gray-400/5 backdrop-blur-[3px] modal-backdrop-in">
          <div className="w-full max-w-lg rounded-xl bg-white p-6 dark:bg-gray-900 shadow-xl max-h-[85vh] overflow-y-auto modal-content-in">
            <h3 className="text-lg font-semibold text-gray-900 dark:text-white mb-2">
              Rotacionar segredo HMAC
            </h3>

            {!rotateNewSecret && (
              <>
                <div className="rounded-lg border border-warning-200 bg-warning-50 dark:border-warning-500/30 dark:bg-warning-500/10 p-3 mb-4">
                  <p className="text-xs text-warning-700 dark:text-warning-300 leading-relaxed">
                    <strong>Atenção:</strong> a substituição é{" "}
                    <strong>imediata</strong>, sem período de graça. Antes de
                    continuar:
                  </p>
                  <ol className="text-xs text-warning-700 dark:text-warning-300 mt-2 list-decimal list-inside space-y-0.5">
                    <li>
                      Configure seu servidor para aceitar o novo segredo (ainda
                      não temos ele).
                    </li>
                    <li>
                      Vamos enviar um POST de verificação assinado com o novo
                      segredo.
                    </li>
                    <li>Se seu servidor não responder 200, a rotação é abortada.</li>
                  </ol>
                </div>

                {rotateError && (
                  <div className="mb-3 p-3 rounded-lg bg-error-50 dark:bg-error-500/10 text-error-700 dark:text-error-400 text-sm whitespace-pre-line">
                    {rotateError}
                  </div>
                )}

                <div className="flex justify-end gap-3 pt-2">
                  <button
                    type="button"
                    onClick={closeRotate}
                    disabled={rotateRunning}
                    className="rounded-lg px-4 py-2 text-sm font-medium text-gray-600 hover:bg-gray-100 dark:text-gray-400 dark:hover:bg-gray-800 disabled:opacity-50"
                  >
                    Cancelar
                  </button>
                  <button
                    type="button"
                    onClick={runRotate}
                    disabled={rotateRunning}
                    className="rounded-lg bg-error-600 px-4 py-2 text-sm font-medium text-white hover:bg-error-700 disabled:opacity-50"
                  >
                    {rotateRunning ? "Verificando..." : "Rotacionar agora"}
                  </button>
                </div>
              </>
            )}

            {rotateNewSecret && (
              <>
                <div className="rounded-lg border border-success-200 bg-success-50 dark:border-success-500/30 dark:bg-success-500/10 p-3 mb-4">
                  <p className="text-sm font-semibold text-success-700 dark:text-success-300">
                    ✓ Segredo rotacionado
                  </p>
                  <p className="text-xs text-success-700 dark:text-success-300 mt-1">
                    Esta é a <strong>única</strong> oportunidade de copiar o novo
                    segredo. Depois de fechar este modal, ele só fica criptografado.
                  </p>
                </div>

                <label className="block text-sm font-medium text-gray-700 dark:text-gray-300 mb-1">
                  Novo segredo
                </label>
                <div className="flex items-stretch gap-2">
                  <input
                    type="text"
                    value={rotateNewSecret}
                    readOnly
                    className="flex-1 rounded-lg border border-gray-200 px-4 py-2.5 text-sm font-mono dark:border-gray-700 dark:bg-gray-800 dark:text-white"
                  />
                  <button
                    type="button"
                    onClick={copySecret}
                    className="rounded-lg bg-brand-500 px-4 py-2 text-sm font-medium text-white hover:bg-brand-600 whitespace-nowrap"
                  >
                    {rotateCopied ? "Copiado ✓" : "Copiar"}
                  </button>
                </div>

                <div className="flex justify-end gap-3 pt-4">
                  <button
                    type="button"
                    onClick={closeRotate}
                    className="rounded-lg bg-brand-500 px-4 py-2 text-sm font-medium text-white hover:bg-brand-600"
                  >
                    Fechar
                  </button>
                </div>
              </>
            )}
          </div>
        </div>
      )}

      <ConfirmModal
        isOpen={pendingAction !== null}
        title={pendingAction?.title ?? ""}
        message={pendingAction?.message ?? ""}
        confirmLabel={pendingAction?.confirmLabel}
        variant="danger"
        requireCode
        isLoading={pendingRunning}
        onCancel={() => {
          if (!pendingRunning) setPendingAction(null);
        }}
        onConfirm={runPending}
      />
    </div>
  );
}
