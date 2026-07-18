"use client";

import { useEffect } from "react";
import Link from "next/link";
import { Illustration } from "@/components/ui/Illustration";

/**
 * Error boundary do app router (Next.js 16). Capturada por todas as routes
 * sob /, exceto layout/root errors (que usam global-error.tsx separado).
 *
 * Apresenta uma illustration de server error + microcópia que reconhece o
 * problema sem alarmar + CTA "tentar novamente" via `reset` (re-renderiza
 * o segment que falhou) + fallback "voltar ao início" para casos sem
 * caminho de retry.
 */
export default function GlobalError({
  error,
  reset,
}: {
  error: Error & { digest?: string };
  reset: () => void;
}) {
  useEffect(() => {
    // Não logamos no console em prod (Sentry/Logflare cuidam). Em dev,
    // ajuda a diagnosticar sem precisar abrir react-devtools.
    if (process.env.NODE_ENV !== "production") {
      console.error("[app/error.tsx]", error);
    }
  }, [error]);

  return (
    <div className="relative flex flex-col items-center justify-center min-h-screen p-6 bg-gray-50 dark:bg-gray-900">
      <div className="mx-auto w-full max-w-md text-center">
        <Illustration
          name="server-error"
          size="xl"
          className="mx-auto mb-8"
        />

        <h1 className="text-2xl font-bold text-gray-900 dark:text-white">
          Algo deu errado
        </h1>
        <p className="mt-3 text-sm text-gray-500 dark:text-gray-400">
          Encontramos um erro inesperado ao processar esta página. Tente
          novamente em instantes — nosso time já foi notificado.
        </p>

        {/* Digest (id do erro) só em dev para facilitar debug. Em prod
            ocultamos para não expor sinal interno ao usuário final. */}
        {error.digest && process.env.NODE_ENV !== "production" && (
          <p className="mt-4 text-[10px] font-mono text-gray-400 dark:text-gray-500 break-all">
            ID: {error.digest}
          </p>
        )}

        <div className="mt-8 flex items-center justify-center gap-2">
          <button
            type="button"
            onClick={reset}
            className="inline-flex items-center justify-center h-11 rounded-xl bg-brand-500 hover:bg-brand-600 px-5 text-sm font-semibold text-white shadow-sm shadow-brand-500/20 transition-all"
          >
            Tentar novamente
          </button>
          <Link
            href="/"
            className="inline-flex items-center justify-center h-11 rounded-xl border border-gray-200 dark:border-gray-700 bg-white dark:bg-gray-900 px-5 text-sm font-semibold text-gray-700 dark:text-gray-300 hover:bg-gray-50 dark:hover:bg-gray-800 transition-colors"
          >
            Ir ao início
          </Link>
        </div>
      </div>

      <p className="absolute bottom-6 left-1/2 -translate-x-1/2 text-xs text-gray-400 dark:text-gray-500">
        © {new Date().getFullYear()} Fellow Pay
      </p>
    </div>
  );
}
