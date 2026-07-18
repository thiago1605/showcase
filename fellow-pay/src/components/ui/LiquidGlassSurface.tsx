"use client";
import React from "react";

interface LiquidGlassSurfaceProps {
  children: React.ReactNode;
  /** Classes adicionais aplicadas no wrapper externo (posicionamento, etc). */
  className?: string;
  /** Tailwind rounded class — também aplicado no clipping container. Default rounded-3xl. */
  rounded?: string;
  /** Ativa o bounce-on-hover (transition all 400ms cubic-bezier overshoot). */
  bounce?: boolean;
  /**
   * Cor do tint (camada 2) — wash que sobrepõe o backdrop blur. Default
   * `null` = sem tint, preserva as cores do conteúdo do popup (ex.:
   * UserDropdown com header roxo + body branco mantém sua paleta, só ganha
   * shine + blur). Pra adicionar tint use ex.: "rgba(255,255,255,0.25)"
   * (branco), "rgba(139,71,217,0.15)" (brand), etc.
   */
  tint?: string | null;
  /** Estilos inline custom (mergeados com o shadow + transition internos). */
  style?: React.CSSProperties;
  /**
   * Quando `true`, remove o drop shadow externo + reduz drasticamente o
   * shine inset. Use pra elementos que devem se INTEGRAR ao layout (ex.:
   * header) e não destacar como "card flutuante" (que é o default).
   */
  subtle?: boolean;
}

/**
 * Wrapper reusável que aplica o efeito liquid-glass macOS-style em qualquer
 * popup/dropdown/menu flutuante. Implementa as 4 layers do
 * lucasromerodb/liquid-glass-effect-macos:
 *
 *   1. **effect**: backdrop-filter blur + filter:url(#fellow-liquid-glass)
 *      → captura e DISTORCE o que tá atrás (via SVG turbulence + displacement
 *      map, não só blur)
 *   2. **tint**: rgba(255,255,255,0.25) → cor base do "vidro"
 *   3. **shine**: dual inset box-shadows → reflexo de luz nas bordas
 *   4. **content**: relative z-[3] → children renderizam por cima
 *
 * O outer NÃO tem overflow:hidden — popovers/sub-dropdowns escapam livremente.
 * O clipping wrapper interno (pointer-events:none) clipa só as glass layers
 * ao rounded.
 *
 * Quando `bounce`, aplica `transition: all 400ms cubic-bezier(0.175,0.885,0.32,2.2)`
 * — qualquer mudança de propriedade (padding/scale/etc) em hover anima com
 * overshoot iOS-style.
 *
 * Pré-requisito: `<LiquidGlassFilter />` precisa estar montado em algum lugar
 * do app (root layout) pra o SVG filter `#fellow-liquid-glass` existir no DOM.
 */
export function LiquidGlassSurface({
  children,
  className = "",
  rounded = "rounded-3xl",
  bounce = true,
  tint = null,
  style,
  subtle = false,
}: LiquidGlassSurfaceProps) {
  // Só aplica `relative` se o consumer não passou OUTRA posição (absolute,
  // fixed, sticky) — senão Tailwind resolve conflito de position de forma
  // imprevisível e o consumer perde sua posição (gerou bug onde Dropdown
  // virava normal-flow e empurrava o header).
  const hasPosition = /\b(absolute|fixed|sticky|relative)\b/.test(className);
  const positionClass = hasPosition ? "" : "relative";
  return (
    <div
      className={`${positionClass} ${rounded} ${className}`}
      style={{
        boxShadow: subtle
          ? "none"
          : "0 6px 6px rgba(0,0,0,0.12), 0 0 20px rgba(0,0,0,0.06)",
        ...(bounce
          ? {
              transition: "all 0.4s cubic-bezier(0.175, 0.885, 0.32, 2.2)",
            }
          : {}),
        ...style,
      }}
    >
      {/* Clipping container — clipa só as 3 glass layers ao rounded.
          `borderRadius: inherit` pega o radius do outer (que pode ser
          sobrescrito por className), garantindo que glass e outer alinhem.
          pointer-events:none deixa cliques passarem pro content. */}
      <div
        className="absolute inset-0 overflow-hidden pointer-events-none"
        style={{ borderRadius: "inherit" }}
      >
        <div
          aria-hidden="true"
          className="absolute inset-0 z-0"
          style={{
            backdropFilter: "blur(3px)",
            WebkitBackdropFilter: "blur(3px)",
            filter: "url(#fellow-liquid-glass)",
            isolation: "isolate",
          }}
        />
        {tint && (
          <div
            aria-hidden="true"
            className="absolute inset-0 z-[1]"
            style={{ background: tint }}
          />
        )}
        <div
          aria-hidden="true"
          className="absolute inset-0 z-[2]"
          style={{
            boxShadow: subtle
              ? "inset 0 -1px 0 0 rgba(255,255,255,0.15)"
              : "inset 2px 2px 1px 0 rgba(255,255,255,0.5), inset -1px -1px 1px 1px rgba(255,255,255,0.5)",
          }}
        />
      </div>
      <div className="relative z-[3]">{children}</div>
    </div>
  );
}
