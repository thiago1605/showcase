"use client";
import { useEffect } from "react";

/**
 * Bloqueia o scroll da página enquanto o modal/overlay estiver aberto.
 *
 * Quando `isLocked === true`:
 *   - salva o `overflow` atual do `<body>` (pra restaurar no unmount)
 *   - aplica `overflow: hidden` impedindo scroll
 *
 * Quando `isLocked === false` ou ao desmontar:
 *   - restaura o valor original do `overflow`
 *
 * Empilhamento: se múltiplos modais abrirem juntos (raro mas possível),
 * cada `useScrollLock(true)` é idempotente — `overflow: hidden` continua
 * aplicado até o último fechar. O valor original é salvo no FIRST mount
 * que ativa o lock.
 */
export function useScrollLock(isLocked: boolean) {
  useEffect(() => {
    if (!isLocked) return;
    const original = document.body.style.overflow;
    document.body.style.overflow = "hidden";
    return () => {
      document.body.style.overflow = original;
    };
  }, [isLocked]);
}
