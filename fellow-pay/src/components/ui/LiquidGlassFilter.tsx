"use client";
import React from "react";

/**
 * SVG turbulence filter usado pelo efeito liquid-glass macOS-style.
 * Renderizado uma única vez no root layout (oculto) e referenciado por
 * qualquer popup via `filter: url(#fellow-liquid-glass)`.
 *
 * Implementação portada de:
 * https://github.com/lucasromerodb/liquid-glass-effect-macos
 *
 * O segredo do efeito "líquido" é o `feDisplacementMap` com scale=150 que
 * USA o fractal noise da turbulence pra warpar a imagem capturada via
 * `backdrop-filter`. Não é só blur — é distorção orgânica.
 */
export function LiquidGlassFilter() {
  return (
    <svg className="fixed -z-10 w-0 h-0 pointer-events-none" aria-hidden="true">
      <filter id="fellow-liquid-glass" x="0%" y="0%" width="100%" height="100%" filterUnits="objectBoundingBox">
        <feTurbulence type="fractalNoise" baseFrequency="0.01 0.01" numOctaves={1} seed={5} result="turbulence" />
        <feComponentTransfer in="turbulence" result="mapped">
          <feFuncR type="gamma" amplitude={1} exponent={10} offset={0.5} />
          <feFuncG type="gamma" amplitude={0} exponent={1} offset={0} />
          <feFuncB type="gamma" amplitude={0} exponent={1} offset={0.5} />
        </feComponentTransfer>
        <feGaussianBlur in="turbulence" stdDeviation={3} result="softMap" />
        <feSpecularLighting in="softMap" surfaceScale={5} specularConstant={1} specularExponent={100} lightingColor="white" result="specLight">
          <fePointLight x={-200} y={-200} z={300} />
        </feSpecularLighting>
        <feComposite in="specLight" operator="arithmetic" k1={0} k2={1} k3={1} k4={0} result="litImage" />
        <feDisplacementMap in="SourceGraphic" in2="softMap" scale={150} xChannelSelector="R" yChannelSelector="G" />
      </filter>
    </svg>
  );
}
