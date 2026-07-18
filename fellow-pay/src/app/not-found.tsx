import Link from "next/link";
import { Illustration } from "@/components/ui/Illustration";

/**
 * Página 404 — usa o sistema de Illustrations centralizado em vez de assets
 * estáticos em /public/images. Mantém consistência visual com empty states e
 * outras telas de exceção do app.
 */
export default function NotFound() {
  return (
    <div className="relative flex flex-col items-center justify-center min-h-screen p-6 bg-gray-50 dark:bg-gray-900">
      <div className="mx-auto w-full max-w-md text-center">
        <Illustration
          name="not-found-404"
          size="xl"
          className="mx-auto mb-8"
        />

        <h1 className="text-2xl font-bold text-gray-900 dark:text-white">
          Página não encontrada
        </h1>
        <p className="mt-3 text-sm text-gray-500 dark:text-gray-400">
          O link que você acessou não existe, foi removido ou está temporariamente indisponível.
        </p>

        <div className="mt-8 flex items-center justify-center gap-2">
          <Link
            href="/"
            className="inline-flex items-center justify-center h-11 rounded-xl bg-brand-500 hover:bg-brand-600 px-5 text-sm font-semibold text-white shadow-sm shadow-brand-500/20 transition-all"
          >
            Voltar ao início
          </Link>
        </div>
      </div>

      <p className="absolute bottom-6 left-1/2 -translate-x-1/2 text-xs text-gray-400 dark:text-gray-500">
        © {new Date().getFullYear()} Fellow Pay
      </p>
    </div>
  );
}
