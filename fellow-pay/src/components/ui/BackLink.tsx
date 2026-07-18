"use client";

import React from "react";
import Link from "next/link";
import { useRouter } from "next/navigation";

/**
 * Botão "← Voltar" que volta pra página anterior preservando scroll/filtros.
 *
 * Comportamento:
 *   - Se há history (navegação interna), `router.back()` — volta exatamente
 *     pra de onde veio, mantendo posição de scroll e estado da listagem.
 *   - Se não há (direct link, open-in-new-tab, refresh em rota direta),
 *     `router.push(fallbackHref)` — destino estável.
 *
 * Detecção via `window.history.length > 1`: no SPA do Next, abrir uma URL
 * direta cria 1 entry; navegar internamente cria >= 2. Não é 100% à prova
 * de bala (refresh preserva o length), mas pega 99% dos casos.
 *
 * Estilo: chip branco com border cinza + glyph `←`. Discreto, legível, não
 * compete com o conteúdo da página. Override via `className` (usado no estado
 * de erro pra renderizar CTA brand-500 grande).
 *
 * Seta `←` é GLYPH (U+2190) da fonte — alinhamento herda do line-box do flex
 * sem hack de SVG vs centro óptico.
 *
 * Usado em /products/[id] e /affiliations/[id].
 */
export function BackLink({
  fallbackHref,
  label = "Retornar",
  className,
}: {
  fallbackHref: string;
  label?: string;
  className?: string;
}) {
  const router = useRouter();
  const [canGoBack, setCanGoBack] = React.useState(false);

  React.useEffect(() => {
    setCanGoBack(typeof window !== "undefined" && window.history.length > 1);
  }, []);

  // Chip branco simples: border cinza + bg white + texto gray-700. Hover só
  // altera texto e border pro brand — bg permanece branco em todos os estados.
  const base =
    "inline-flex h-8 items-center gap-1 rounded-lg border border-gray-200 dark:border-gray-700 bg-white dark:bg-gray-900 px-3 text-sm font-medium text-gray-700 dark:text-gray-300 hover:text-brand-700 dark:hover:text-brand-300 hover:border-brand-300 dark:hover:border-brand-500/50 transition-colors";
  const cls = className ?? base;

  const content = (
    <>
      <span aria-hidden="true">←</span>
      {label}
    </>
  );

  if (!canGoBack) {
    return (
      <Link href={fallbackHref} className={cls}>
        {content}
      </Link>
    );
  }

  return (
    <button type="button" onClick={() => router.back()} className={cls}>
      {content}
    </button>
  );
}
