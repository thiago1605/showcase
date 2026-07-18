"use client";
import React, { useEffect, useLayoutEffect, useMemo, useRef, useState } from "react";
import { useQueryClient, useIsFetching } from "@tanstack/react-query";
import { useDashboardPeriod, PeriodPreset } from "./PeriodContext";
import { ExportButton } from "./ExportButton";
import { useTheme } from "@/context/ThemeContext";

// Cooldown entre cliques no refresh — `useIsFetching` só desabilita o botão
// enquanto requests estão em voo, mas se a network responde em <100ms o seller
// consegue clicar 10x em sequência e cada clique dispara uma cascata de
// invalidateQueries (que refetcha tudo em paralelo). Isso bate em rate limit
// fácil. 2s é tempo suficiente pra ver o resultado antes do próximo refresh.
const REFRESH_COOLDOWN_MS = 2000;

const PRESETS: { value: Exclude<PeriodPreset, "CUSTOM">; label: string }[] = [
  { value: "TODAY", label: "Hoje" },
  { value: "LAST_7", label: "7 dias" },
  { value: "LAST_30", label: "30 dias" },
  { value: "LAST_90", label: "90 dias" },
];

function formatRange(from: string, to: string): string {
  const fmt = new Intl.DateTimeFormat("pt-BR", { day: "2-digit", month: "short" });
  return `${fmt.format(new Date(from))} – ${fmt.format(new Date(to))}`;
}

function toInputDate(iso: string): string {
  return new Date(iso).toISOString().slice(0, 10);
}

interface PeriodSelectorProps {
  /** Mostra o botão "Exportar" (CSV/PDF). Esses arquivos cobrem o summary +
   *  timeseries do painel principal — em outras páginas (ex: /insights) não
   *  faz sentido exportar esse recorte específico, então o botão fica oculto. */
  showExport?: boolean;
}

export function PeriodSelector({ showExport = true }: PeriodSelectorProps = {}) {
  const { period, setPreset, setCustom } = useDashboardPeriod();
  const queryClient = useQueryClient();
  const fetchingCount = useIsFetching({ queryKey: ["dashboard"] });
  const [showCustom, setShowCustom] = useState(false);
  const [customFrom, setCustomFrom] = useState(toInputDate(period.from));
  const [customTo, setCustomTo] = useState(toInputDate(period.to));
  // Cooldown manual depois do clique — disabled fica true mesmo se o request
  // já terminou, evita burst quando o backend responde rápido.
  const [coolingDown, setCoolingDown] = useState(false);
  const cooldownTimerRef = useRef<ReturnType<typeof setTimeout> | null>(null);

  // Limpa timer em unmount pra evitar setState em componente desmontado.
  useEffect(() => {
    return () => {
      if (cooldownTimerRef.current) clearTimeout(cooldownTimerRef.current);
    };
  }, []);

  // Detecta o estado "stuck": quando o sticky parent ativa (rect.top <= 73px,
  // que é nosso offset de top-[72px] + 1px de tolerância). Quando stuck,
  // ativamos os 4 layers do liquid glass. Em posição normal (topo da página),
  // o container fica com bg sólido — o efeito glass só faz sentido quando há
  // conteúdo passando atrás dele.
  const containerRef = useRef<HTMLDivElement | null>(null);
  const [isStuck, setIsStuck] = useState(false);
  // Tint conditional pelo tema — branco translúcido em light, purple-dark
  // (gray-purple) translúcido em dark. Sem isso o glass do PeriodSelector
  // sticky fica esbranquiçado/luminoso no dark mode.
  const { theme } = useTheme();
  const glassTint =
    theme === "dark" ? "rgba(46,26,79,0.35)" : "rgba(255, 255, 255, 0.25)";
  useEffect(() => {
    const node = containerRef.current;
    if (!node) return;
    const handleScroll = () => {
      const rect = node.getBoundingClientRect();
      setIsStuck(rect.top <= 73);
    };
    handleScroll();
    window.addEventListener("scroll", handleScroll, { passive: true });
    return () => window.removeEventListener("scroll", handleScroll);
  }, []);

  const refreshDisabled = fetchingCount > 0 || coolingDown;

  const handleRefresh = () => {
    // Guarda defensiva — também ignora se ainda em cooldown ou request pending.
    if (refreshDisabled) return;
    setCoolingDown(true);
    queryClient.invalidateQueries({ queryKey: ["dashboard"] });
    cooldownTimerRef.current = setTimeout(() => {
      setCoolingDown(false);
    }, REFRESH_COOLDOWN_MS);
  };

  const applyCustom = () => {
    if (!customFrom || !customTo) return;
    const from = new Date(customFrom);
    from.setHours(0, 0, 0, 0);
    const to = new Date(customTo);
    to.setHours(23, 59, 59, 999);
    setCustom(from.toISOString(), to.toISOString());
    setShowCustom(false);
  };

  return (
    // Container muda de estilo conforme `isStuck`:
    // - Normal (topo da página): bg sólido + border padrão (estado de
    //   repouso, sem efeitos)
    // - Stuck (rolagem ativa o sticky): liquid glass macOS-style — 4 layers
    //   empilhadas (effect / tint / shine / content) com SVG turbulence
    //   filter pra distorção líquida real (adaptado de
    //   github.com/lucasromerodb/liquid-glass-effect-macos)
    <div
      ref={containerRef}
      className={`relative rounded-3xl p-3 transition-shadow duration-300 ${
        isStuck
          ? ""
          : "border border-gray-200/60 bg-white dark:border-gray-800 dark:bg-gray-900"
      }`}
      style={
        isStuck
          ? { boxShadow: "0 6px 6px rgba(0,0,0,0.12), 0 0 20px rgba(0,0,0,0.06)" }
          : undefined
      }
    >
      {/* Clipping container só para os 3 layers de glass — `overflow-hidden`
          aqui garante que o blur/filter/shine respeitem o rounded-3xl. Fora
          desse wrapper, o content fica livre pra dropdowns/popovers
          (Exportar, etc.) escaparem dos bounds sem clipping. Só renderiza
          quando stuck. */}
      {isStuck && (
        <div className="absolute inset-0 rounded-3xl overflow-hidden pointer-events-none">
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
          <div
            aria-hidden="true"
            className="absolute inset-0 z-[1]"
            style={{ background: glassTint }}
          />
          <div
            aria-hidden="true"
            className="absolute inset-0 z-[2]"
            style={{
              // Shine condicional pelo tema: em light mode usa o highlight
              // branco padrão (refração de bolha); em dark mode reduz drasticamente
              // (alpha 0.12 vs 0.5) — bordas brancas brilhantes ficam feias sobre
              // o gray-purple translúcido escuro.
              boxShadow:
                theme === "dark"
                  ? "inset 1px 1px 0 0 rgba(255,255,255,0.12), inset -1px -1px 0 0 rgba(255,255,255,0.06)"
                  : "inset 2px 2px 1px 0 rgba(255,255,255,0.5), inset -1px -1px 1px 1px rgba(255,255,255,0.5)",
            }}
          />
        </div>
      )}
      {/* Content layer — z-[3] (acima dos glass layers) sem overflow:hidden
          herdado, então popovers do ExportButton podem estourar pra fora. */}
      <div className="relative z-[3]">
      <div className="flex flex-wrap items-center justify-between gap-3">
        <SegmentedPresets
          activeKey={period.preset === "CUSTOM" ? "CUSTOM" : period.preset}
          onSelect={(key) => {
            if (key === "CUSTOM") {
              setShowCustom((v) => !v);
            } else {
              setPreset(key);
              setShowCustom(false);
            }
          }}
        />
        <div className="flex items-center gap-3">
          <span className="text-xs text-gray-500 dark:text-gray-400 hidden sm:inline">
            {formatRange(period.from, period.to)}
          </span>
          {showExport && <ExportButton />}
          <button
            type="button"
            onClick={handleRefresh}
            disabled={refreshDisabled}
            aria-label="Atualizar dados da dashboard"
            title={
              coolingDown
                ? "Aguarde um momento antes de atualizar de novo"
                : "Atualizar dados"
            }
            className="inline-flex items-center justify-center w-8 h-8 rounded-lg text-gray-500 hover:bg-gray-100 hover:text-gray-700 dark:text-gray-400 dark:hover:bg-gray-800 dark:hover:text-gray-200 disabled:opacity-50 disabled:cursor-not-allowed transition-colors"
          >
            <svg
              width="14"
              height="14"
              viewBox="0 0 14 14"
              fill="none"
              className={fetchingCount > 0 ? "animate-spin" : undefined}
              aria-hidden="true"
            >
              <path
                d="M12.25 7A5.25 5.25 0 1 1 7 1.75c1.85 0 3.475.962 4.408 2.412M12.25 1.75v3h-3"
                stroke="currentColor"
                strokeWidth="1.4"
                strokeLinecap="round"
                strokeLinejoin="round"
              />
            </svg>
          </button>
        </div>
      </div>

      {showCustom && (
        <div className="mt-3 flex flex-wrap items-end gap-3 border-t border-gray-100 pt-3 dark:border-gray-800">
          <div>
            <label className="block text-xs text-gray-500 dark:text-gray-400 mb-1">De</label>
            <input
              type="date"
              value={customFrom}
              onChange={(e) => setCustomFrom(e.target.value)}
              max={customTo}
              className="rounded-lg border border-gray-200 px-3 py-1.5 text-sm dark:border-gray-700 dark:bg-gray-800 dark:text-white focus:border-brand-500 focus:outline-none"
            />
          </div>
          <div>
            <label className="block text-xs text-gray-500 dark:text-gray-400 mb-1">Até</label>
            <input
              type="date"
              value={customTo}
              onChange={(e) => setCustomTo(e.target.value)}
              min={customFrom}
              className="rounded-lg border border-gray-200 px-3 py-1.5 text-sm dark:border-gray-700 dark:bg-gray-800 dark:text-white focus:border-brand-500 focus:outline-none"
            />
          </div>
          <button
            onClick={applyCustom}
            className="rounded-lg bg-brand-500 hover:bg-brand-600 px-4 py-1.5 text-xs font-medium text-white transition-colors"
          >
            Aplicar
          </button>
        </div>
      )}
      </div>
      {/* (SVG filter #fellow-liquid-glass mora globalmente em RootLayout
          via <LiquidGlassFilter />.) */}
    </div>
  );
}

/* ------------------------------ SegmentedPresets ----------------------------- *
 * Toggle pill com indicador deslizante. Mede o botão ativo via ref e posiciona
 * um <div> absoluto com o gradient — animado via CSS transition on
 * left/width/height. Resultado: ao trocar de preset, o pill brand "desliza"
 * entre as opções (estilo iOS segmented control) ao invés do bg pular nó
 * a nó. Acessibilidade: cada botão segue sendo um <button> nativo, indicador
 * é puramente decorativo (`aria-hidden`). */
type PresetKey = Exclude<PeriodPreset, "CUSTOM"> | "CUSTOM";
interface SegmentedPresetsProps {
  activeKey: PresetKey;
  onSelect: (key: PresetKey) => void;
}
function SegmentedPresets({ activeKey, onSelect }: SegmentedPresetsProps) {
  // Memoizado pra estabilidade de referência — sem isso, `allOptions` é nova
  // array a cada render, e a dep do useLayoutEffect dispara em loop infinito
  // (setIndicator → re-render → nova allOptions → effect roda → ...).
  const allOptions = useMemo<{ key: PresetKey; label: string }[]>(
    () => [
      ...PRESETS.map((p) => ({ key: p.value as PresetKey, label: p.label })),
      { key: "CUSTOM" as PresetKey, label: "Personalizado" },
    ],
    []
  );

  const trackRef = useRef<HTMLDivElement | null>(null);
  const btnRefs = useRef<(HTMLButtonElement | null)[]>([]);
  const [indicator, setIndicator] = useState<{
    left: number;
    top: number;
    width: number;
    height: number;
    visible: boolean;
  }>({ left: 0, top: 0, width: 0, height: 0, visible: false });

  // useLayoutEffect — mede DOM ANTES do paint pra evitar flicker no primeiro
  // render. Recalcula no resize do window pra acompanhar wrap em mobile.
  useLayoutEffect(() => {
    const activeIdx = allOptions.findIndex((o) => o.key === activeKey);
    const measure = () => {
      const btn = btnRefs.current[activeIdx];
      const track = trackRef.current;
      if (!btn || !track) return;
      // offsetLeft/Top relativos ao parent já posicionado (track tem `relative`).
      setIndicator({
        left: btn.offsetLeft,
        top: btn.offsetTop,
        width: btn.offsetWidth,
        height: btn.offsetHeight,
        visible: true,
      });
    };
    measure();
    window.addEventListener("resize", measure);
    return () => window.removeEventListener("resize", measure);
  }, [activeKey, allOptions]);

  return (
    <div
      ref={trackRef}
      className="relative inline-flex flex-wrap items-center gap-1 rounded-full bg-gray-100/70 dark:bg-gray-800/60 p-1"
    >
      {/* Indicador deslizante — posicionado em coordinates absolutas, animado
          via transition. cubic-bezier(0.22, 1, 0.36, 1) = ease-out suave
          (mesma curva dos dropdowns). 280ms casa com a animação dos popups. */}
      <div
        aria-hidden="true"
        className="pill-gradient-brand absolute rounded-full pointer-events-none"
        style={{
          left: indicator.left,
          top: indicator.top,
          width: indicator.width,
          height: indicator.height,
          opacity: indicator.visible ? 1 : 0,
          transition:
            "left 0.28s cubic-bezier(0.22, 1, 0.36, 1), top 0.28s cubic-bezier(0.22, 1, 0.36, 1), width 0.28s cubic-bezier(0.22, 1, 0.36, 1), height 0.28s cubic-bezier(0.22, 1, 0.36, 1), opacity 0.15s ease-out",
        }}
      />
      {allOptions.map((opt, i) => {
        const active = opt.key === activeKey;
        return (
          <button
            key={opt.key}
            ref={(el) => {
              btnRefs.current[i] = el;
            }}
            onClick={() => onSelect(opt.key)}
            className={`relative rounded-full px-3 py-1.5 text-xs font-medium transition-colors ${
              active
                ? "text-white"
                : "text-gray-600 hover:text-gray-900 dark:text-gray-300 dark:hover:text-white"
            }`}
          >
            {opt.label}
          </button>
        );
      })}
    </div>
  );
}
