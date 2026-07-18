"use client";

import { useRef, useState, type MouseEvent as ReactMouseEvent } from "react";
import { useTheme } from "@/context/ThemeContext";
import type { SellerTierCode } from "@/types";

/** Máximo de rotação por eixo (graus). 8° é sweet spot — tilt visível
 *  sem ficar nauseante. Cards reais físicos chegam até ~15° em sites
 *  premium, mas pra fintech sóbrio (Bloomberg/F1) 8° fica refinado. */
const MAX_TILT_DEG = 8;

/** Perspectiva 3D (px). Menor = mais dramático/distorção; maior = mais sutil.
 *  1000px = Amex/Apple feel. */
const PERSPECTIVE_PX = 1000;

/**
 * Visual de cartão físico (aspect-ratio 1.586:1, ISO 7810 ID-1) pro nível
 * vigente do seller. Inspirado em Amex Centurion / Visa Infinite — gradient
 * metálico por tier + shimmer overlay + chip icon + Fellow logo.
 *
 * Não é interativo (só visual). Aparece no hero da página /tier acima dos
 * stats (volume + barra de progresso). Reforça o status de forma tátil,
 * alinhado com a posição premium do produto.
 */

interface TierPremiumCardProps {
  tier: SellerTierCode;
  sellerName: string;
  foundingNumber?: number | null;
  /**
   * Tamanho do card:
   *  - "md" (default): hero da página /tier — full size, tilt 3D, shine sweep,
   *    spotlight tracking, hover lift.
   *  - "sm": widget do dashboard — versão compacta sem interatividade
   *    (sem tilt/shine/spotlight) com fontes e padding reduzidos. Sempre
   *    `w-full` (preenche o container do widget).
   */
  size?: "md" | "sm";
}

interface CardTheme {
  /** Container do cartão — gradient base + ring + shadow. */
  cls: string;
  /** Cor do texto principal (label do nível). */
  labelCls: string;
  /** Cor do texto secundário (nome do seller, Pioneer #). */
  subtleCls: string;
  /** Overlay extra (shimmer/holographic) — null se não aplicável. */
  overlay?: string;
  /** Texto "Fellow Pay" no topo do cartão. */
  brandCls: string;
}

const TIER_LABEL: Record<SellerTierCode, string> = {
  SILVER: "Silver",
  GOLD: "Gold",
  DIAMOND: "Diamond",
  BLACK: "Black",
  INFINITE: "Infinite",
};

const CARD_THEMES: Record<SellerTierCode, CardTheme> = {
  SILVER: {
    // Light: brushed aluminum (slate-300 → gray-100 → slate-400). Center brilhante = highlight especular.
    // Dark: gunmetal escuro (slate-900 → slate-500 → slate-800) — edge bem escuro, center medium gray.
    // Tentamos slate-300 no center mas o canto inferior-esquerdo (onde fica "Pioneiro") cai numa zona
    // bright demais e texto claro perde contraste. slate-500 mantém shimmer metálico mas preserva
    // legibilidade do texto claro em qualquer ponto do card.
    cls: "bg-gradient-to-br from-slate-300 via-gray-100 to-slate-400 ring-1 ring-slate-400/60 shadow-xl shadow-slate-300/50 dark:from-slate-900 dark:via-slate-500 dark:to-slate-800 dark:ring-slate-500/50 dark:shadow-slate-900/50",
    labelCls: "text-gray-900 dark:text-slate-50",
    subtleCls: "text-gray-700 dark:text-slate-100",
    brandCls: "text-gray-800 dark:text-slate-50",
  },
  GOLD: {
    // Light: antique gold polido (amber-600 → yellow-200 → amber-700) — center yellow-200 cria highlight.
    // Dark: ouro envelhecido com reflexo (amber-900 → yellow-400 → amber-800) — center yellow-400
    // simula luz batendo no centro do metal. As bordas escuras seguram a profundidade.
    cls: "bg-gradient-to-br from-amber-600 via-yellow-200 to-amber-700 ring-1 ring-amber-700/60 shadow-xl shadow-amber-600/40 dark:from-amber-900 dark:via-yellow-400 dark:to-amber-800 dark:ring-amber-600/50 dark:shadow-amber-900/40",
    labelCls: "text-amber-950 dark:text-amber-100",
    subtleCls: "text-amber-900/80 dark:text-amber-200/80",
    brandCls: "text-amber-950 dark:text-amber-100",
  },
  DIAMOND: {
    // Light: ice crystal (sky-100 → cyan-50 → sky-300) — center cyan-50 cria iridescência.
    // Dark: diamante sob luz (sky-900 → cyan-300 → sky-800) — center cyan-300 brilha como
    // facetação de cristal contra azul profundo das bordas.
    cls: "bg-gradient-to-br from-sky-100 via-cyan-50 to-sky-300 ring-1 ring-sky-400/70 shadow-xl shadow-sky-300/60 dark:from-sky-900 dark:via-cyan-300 dark:to-sky-800 dark:ring-cyan-400/50 dark:shadow-sky-900/40",
    labelCls: "text-sky-950 dark:text-cyan-100",
    subtleCls: "text-sky-900/80 dark:text-cyan-200/80",
    brandCls: "text-sky-950 dark:text-cyan-100",
    // Overlay holographic — em dark mode reduzimos alpha pra não "explodir" brilho.
    // mix-blend-soft-light funciona OK em ambos modos.
    overlay:
      "bg-[conic-gradient(from_135deg_at_50%_50%,rgba(186,230,253,0.40),rgba(199,210,254,0.35),rgba(252,165,165,0.25),rgba(254,240,138,0.25),rgba(167,243,208,0.30),rgba(186,230,253,0.40))] mix-blend-soft-light dark:opacity-50",
  },
  BLACK: {
    // Black já é dark-friendly por design — só ajusta ring/shadow no dark mode.
    cls: "bg-gradient-to-br from-gray-900 via-black to-gray-800 ring-1 ring-amber-500/40 shadow-2xl shadow-black/50 dark:ring-amber-500/60 dark:shadow-black",
    labelCls: "text-amber-100",
    subtleCls: "text-amber-200/70",
    brandCls: "text-amber-100/90",
  },
  INFINITE: {
    // Infinite usa cor de marca (roxo Fellow) — funciona bem em ambos modos.
    // Só leve ajuste do glow shadow pra não ofuscar no dark.
    cls: "bg-gradient-to-br from-brand-700 via-brand-500 to-purple-800 ring-1 ring-brand-300/60 shadow-2xl shadow-brand-500/50 dark:shadow-brand-700/60",
    labelCls: "text-white",
    subtleCls: "text-white/80",
    brandCls: "text-white",
    overlay:
      "bg-[radial-gradient(ellipse_at_top_right,rgba(255,255,255,0.20),transparent_60%)] dark:opacity-60",
  },
};

function StarIcon({ className = "" }: { className?: string }) {
  return (
    <svg
      viewBox="0 0 24 24"
      fill="currentColor"
      className={className}
      aria-hidden="true"
    >
      <path d="M12 .587l3.668 7.43 8.2 1.193-5.934 5.783 1.4 8.166L12 18.897l-7.334 3.862 1.4-8.166L.132 9.21l8.2-1.193z" />
    </svg>
  );
}

export function TierPremiumCard({
  tier,
  sellerName,
  foundingNumber,
  size = "md",
}: TierPremiumCardProps) {
  const theme = CARD_THEMES[tier];
  const label = TIER_LABEL[tier];
  const isCompact = size === "sm";

  // Classes condicionais por tamanho. Em sm reduzimos paddings e fontes pra
  // caber em widget do dashboard sem perder a leitura "cartão de crédito".
  // Cap em 14rem (224px ≈ ⅔ do tamanho real ISO 7810 ID-1) — calibrado pra
  // o layout horizontal do dashboard TierCard (~140px de altura: card ao lado
  // de stats panel). O hero da /tier (md) usa 25rem.
  const containerCls = isCompact ? "w-full max-w-[14rem]" : "w-full max-w-[25rem]";
  const innerPaddingCls = isCompact ? "p-3" : "p-5 md:p-6";
  const brandTextCls = isCompact ? "text-[10px]" : "text-xs";
  const labelTextCls = isCompact ? "text-lg" : "text-2xl md:text-3xl";
  const pioneiroTextCls = isCompact ? "text-[9px]" : "text-[11px]";
  const sellerNameTextCls = isCompact ? "text-[10px]" : "text-xs";
  const starSizeCls = isCompact ? "h-2.5 w-2.5" : "h-3 w-3";

  // Dark mode reduz alphas dos efeitos de luz translúcidos (shine sweep,
  // spotlight) — em fundo escuro, brancos translúcidos ficam muito visíveis
  // porque o contraste é maior que em fundo claro.
  //
  // Tuning por tier do shine sweep:
  //  - Black: gradient quase preto puro amplifica brancos translúcidos
  //    demais nos dois modos. Usa 0.07 em ambos.
  //  - Infinite em light: roxo brand-500 médio contrasta forte com o
  //    sweep branco — reduz pra 0.30 (entre o 0.45 padrão light e o 0.18
  //    do dark) pra suavizar sem perder o polimento.
  //  - Outros tiers: padrão light 0.45 / dark 0.18.
  const { theme: mode } = useTheme();
  const isDark = mode === "dark";
  const isBlackTier = tier === "BLACK";
  const isInfiniteTier = tier === "INFINITE";
  const shineAlpha = isBlackTier
    ? 0.07
    : isInfiniteTier && !isDark
      ? 0.3
      : isDark
        ? 0.18
        : 0.45;
  const spotlightAlpha = isDark ? 0.15 : 0.35;

  // --- 3D tilt: rastreia cursor → calcula rotateX/Y proporcional ---
  const cardRef = useRef<HTMLDivElement>(null);
  const [tilt, setTilt] = useState({ x: 0, y: 0 });
  const [isHovering, setIsHovering] = useState(false);
  // Posição do "ponto de luz" (radial highlight que segue o cursor)
  const [lightPos, setLightPos] = useState({ x: 50, y: 50 });

  // Em sm desativamos todos os efeitos interativos — handlers viram no-op,
  // tilt/lightPos ficam estáticos. useState/useRef continuam registrados
  // (hooks têm que ser unconditional).
  const handleMouseMove = isCompact
    ? undefined
    : (e: ReactMouseEvent<HTMLDivElement>) => {
        const card = cardRef.current;
        if (!card) return;
        const rect = card.getBoundingClientRect();
        // Posição normalizada [-1, 1] do cursor relativo ao centro do cartão
        const nx = (e.clientX - rect.left - rect.width / 2) / (rect.width / 2);
        const ny = (e.clientY - rect.top - rect.height / 2) / (rect.height / 2);
        setTilt({
          // rotateX inverte Y (cursor pra cima ⇒ topo do cartão "vira" pra trás)
          x: ny * -MAX_TILT_DEG,
          // rotateY segue X (cursor pra direita ⇒ cartão "vira" pra direita)
          y: nx * MAX_TILT_DEG,
        });
        // Posição percentual do cursor no cartão (pra spotlight overlay)
        setLightPos({
          x: ((e.clientX - rect.left) / rect.width) * 100,
          y: ((e.clientY - rect.top) / rect.height) * 100,
        });
      };

  const handleMouseEnter = isCompact ? undefined : () => setIsHovering(true);
  const handleMouseLeave = isCompact
    ? undefined
    : () => {
        setIsHovering(false);
        setTilt({ x: 0, y: 0 });
        setLightPos({ x: 50, y: 50 });
      };

  // Transform combina perspective + tilt + lift (quando hovering).
  // Lift compensa parte do que Tailwind hover:translate-y/scale faria antes —
  // movemos pra JS pra evitar conflito com o transform do tilt.
  // Em sm o transform é undefined (sem 3D — card fica plano e estático).
  const transform = isCompact
    ? undefined
    : [
        `perspective(${PERSPECTIVE_PX}px)`,
        `rotateX(${tilt.x}deg)`,
        `rotateY(${tilt.y}deg)`,
        isHovering ? "translateY(-4px) scale(1.02)" : "",
      ]
        .filter(Boolean)
        .join(" ");

  // Transition curta quando tilting (responsivo) vs longa quando volta ao
  // repouso (smooth ease-out de saída).
  const transition =
    tilt.x === 0 && tilt.y === 0
      ? "transform 400ms ease-out"
      : "transform 80ms ease-out";

  return (
    <div
      ref={cardRef}
      onMouseMove={handleMouseMove}
      onMouseEnter={handleMouseEnter}
      onMouseLeave={handleMouseLeave}
      style={
        isCompact
          ? undefined
          : { transform, transition, transformStyle: "preserve-3d" }
      }
      className={`group relative ${containerCls} aspect-[1.586/1] rounded-2xl overflow-hidden cursor-default
        ${isCompact ? "" : "hover:shadow-2xl"}
        ${theme.cls}`}
    >
      {/* Overlay opcional (shimmer/holographic) — sempre presente nos tiers que têm */}
      {theme.overlay && (
        <div
          className={`absolute inset-0 pointer-events-none ${theme.overlay}`}
          aria-hidden="true"
        />
      )}

      {/* Highlight glossy no topo — simula reflexo de luz constante.
          Reduz no dark mode (white/15 → white/8) pra não "exagerar" o brilho
          contra fundo escuro. */}
      <div
        className="absolute inset-x-0 top-0 h-1/2 bg-gradient-to-b from-white/15 to-transparent dark:from-white/8 pointer-events-none"
        aria-hidden="true"
      />

      {/* SPOTLIGHT + SHINE SWEEP — só em md (interativos via hover).
          Em sm o card é estático: sem hover effects, sem cursor tracking. */}
      {!isCompact && (
        <>
          {/* SPOTLIGHT: ponto de luz radial que segue o cursor.
              Alpha varia por modo (0.35 light / 0.15 dark) — dark mode com fundo
              escuro amplifica brancos translúcidos. */}
          <div
            className="absolute inset-0 pointer-events-none opacity-0 group-hover:opacity-100 transition-opacity duration-300"
            style={{
              background: `radial-gradient(circle 200px at ${lightPos.x}% ${lightPos.y}%, rgba(255,255,255,${spotlightAlpha}), transparent 70%)`,
            }}
            aria-hidden="true"
          />

          {/* SHINE SWEEP DIAGONAL: band fino de luz translúcida varre o cartão
              de top-left pra bottom-right em ~25°. Alpha varia por modo
              (0.45 light / 0.18 dark) — em fundo escuro o sweep ficava
              "lanterna passando pelo cartão". */}
          <div
            className="absolute pointer-events-none"
            style={{
              top: "-50%",
              left: "-50%",
              width: "30%",
              height: "200%",
              background: `linear-gradient(to right, transparent, rgba(255,255,255,${shineAlpha}), transparent)`,
              transform: `translateX(${isHovering ? "350%" : "-150%"}) rotate(-25deg)`,
              transition: "transform 800ms ease-out",
              transformOrigin: "center",
            }}
            aria-hidden="true"
          />
        </>
      )}

      <div className={`relative h-full flex flex-col justify-between ${innerPaddingCls}`}>
        {/* TOP: Fellow Pay logo. Chip EMV foi removido — esse card comunica
            status (nível conquistado), não é representação de cartão bancário. */}
        <div className="flex items-start justify-between">
          <div className={`${brandTextCls} font-bold tracking-[0.2em] ${theme.brandCls}`}>
            FELLOW PAY
          </div>
        </div>

        {/* BOTTOM: tier label + seller name + pioneiro.
            Seller name escondido em sm — a 14rem o nome trunca feio e é
            redundante (seller já é o usuário logado, visível no header).
            Em md (hero da /tier) faz sentido manter pra personalizar o card. */}
        <div className="flex items-end justify-between gap-3">
          <div className="min-w-0">
            <div
              className={`${labelTextCls} font-bold tracking-tight ${theme.labelCls}`}
            >
              {label}
            </div>
            {foundingNumber != null && (
              <div
                className={`mt-1 inline-flex items-center gap-1 ${pioneiroTextCls} font-semibold ${theme.subtleCls}`}
              >
                <StarIcon className={starSizeCls} />
                Pioneiro #{String(foundingNumber).padStart(3, "0")}
              </div>
            )}
          </div>
          {!isCompact && (
            <div
              className={`text-right ${sellerNameTextCls} font-medium uppercase tracking-wider truncate max-w-[50%] ${theme.subtleCls}`}
              title={sellerName}
            >
              {sellerName}
            </div>
          )}
        </div>
      </div>
    </div>
  );
}
