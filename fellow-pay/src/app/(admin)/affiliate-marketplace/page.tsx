"use client";

import { useEffect, useMemo, useState } from "react";
import Link from "next/link";
import { useQuery, useMutation, useQueryClient } from "@tanstack/react-query";
import { marketplaceService } from "@/services/marketplace.service";
import { ApiError } from "@/lib/api/client";
import { Select } from "@/components/ui/Select";
import { EmptyChecklistState } from "@/components/marketplace/EmptyChecklistState";
import { Illustration } from "@/components/ui/Illustration";
import { PageHeader } from "@/components/ui/PageHeader";
import { useScrollLock } from "@/hooks/useScrollLock";
import type {
  AffiliationModeCode,
  AffiliationStatusCode,
  Product,
} from "@/types";

/**
 * Catálogo PRIVADO de produtos de outros sellers que estão abertos para
 * afiliação. Equivalente ao "Marketplace de Afiliação" da Kirvano.
 *
 * Filtros: search por nome/descrição/produtor, categoria curada (chip rail),
 * faixa de comissão mínima, modo (OPEN | REQUEST). Sort: recém-publicado |
 * maior comissão | maior preço.
 *
 * Ação: "Solicitar afiliação" → backend valida + cria Affiliation
 * (PENDING ou APPROVED conforme modo).
 */

const MODE_BADGE: Record<
  string,
  { label: string; cls: string; dot: string }
> = {
  OPEN: {
    label: "Afiliação aberta",
    cls: "bg-success-50 text-success-700 ring-success-200/60 dark:bg-success-500/15 dark:text-success-300 dark:ring-success-500/30",
    dot: "bg-success-500",
  },
  REQUEST: {
    label: "Sob aprovação",
    cls: "bg-warning-50 text-warning-700 ring-warning-200/60 dark:bg-warning-500/15 dark:text-warning-300 dark:ring-warning-500/30",
    dot: "bg-warning-500",
  },
  CLOSED: {
    label: "Fechado",
    cls: "bg-gray-100 text-gray-500 ring-gray-200/60 dark:bg-gray-800 dark:text-gray-500 dark:ring-gray-700/60",
    dot: "bg-gray-400",
  },
};

type SortOption = "newest" | "commission" | "price";

function formatBRL(v: number) {
  // Em valores redondos (R$ 1.497,00 → R$ 1.497), omite os decimais para
  // limpar visualmente. Quando há centavos reais (R$ 103,95), mantém.
  const hasCents = Math.round(v * 100) % 100 !== 0;
  return v.toLocaleString("pt-BR", {
    style: "currency",
    currency: "BRL",
    minimumFractionDigits: hasCents ? 2 : 0,
    maximumFractionDigits: 2,
  });
}

function initialsOf(name: string | null | undefined) {
  if (!name) return "?";
  const parts = name.trim().split(/\s+/);
  if (parts.length === 1) return parts[0].slice(0, 2).toUpperCase();
  return (parts[0][0] + parts[parts.length - 1][0]).toUpperCase();
}

const PAGE_SIZE = 12;
// Threshold para o badge "Alta comissão". 30% é razoavelmente acima da
// mediana de cursos digitais (20-30%). Acima disso, vale highlight.
const HIGH_COMMISSION_THRESHOLD = 30;

export default function AffiliateMarketplacePage() {
  const [page, setPage] = useState(1);
  const [search, setSearch] = useState("");
  // Multi-select: array de categorias selecionadas (preserva casing original).
  // Vazio = sem filtro (mostra tudo). Toggle por chip; "Todos" limpa.
  const [selectedCategories, setSelectedCategories] = useState<string[]>([]);
  const [mode, setMode] = useState<AffiliationModeCode | "">("");
  const [minCommission, setMinCommission] = useState("");
  const [sortBy, setSortBy] = useState<SortOption>("newest");
  // Produto aberto no detail modal. Modal vive no nível da page (não no card)
  // para evitar duplicar 1 modal por card e centralizar o controle de foco.
  const [detailProduct, setDetailProduct] = useState<Product | null>(null);

  const { data, isLoading } = useQuery({
    queryKey: ["affiliate-marketplace", { page, selectedCategories, mode }],
    queryFn: () =>
      marketplaceService.listCatalog({
        page,
        pageSize: PAGE_SIZE,
        categories: selectedCategories.length > 0 ? selectedCategories : undefined,
        mode: mode || undefined,
      }),
  });

  // Memoizar para que o array tenha referência estável entre renders e
  // não invalide os useMemo abaixo a cada keystroke (warning do exhaustive-deps).
  const allItems = useMemo(() => data?.items ?? [], [data?.items]);

  // Filtros client-side aplicados sobre a página atual (search + minCommission).
  // Limitação: combina mal com paginação se filtrar muito. Aceitável para MVP —
  // catálogo é tipicamente pequeno (< 100 produtos no mesmo tenant).
  const items = useMemo(() => {
    let result = [...allItems];
    if (search.trim()) {
      const q = search.trim().toLowerCase();
      result = result.filter(
        (p) =>
          p.name.toLowerCase().includes(q) ||
          p.description?.toLowerCase().includes(q) ||
          p.ownerSellerName?.toLowerCase().includes(q),
      );
    }
    if (minCommission) {
      const min = parseFloat(minCommission);
      if (Number.isFinite(min))
        result = result.filter(
          (p) => p.defaultAffiliateCommissionPercent >= min,
        );
    }
    // Sort também client-side. "newest" usa createdAt desc (default da API);
    // "commission" e "price" reordenam sobre o resultado já filtrado.
    if (sortBy === "commission") {
      result = result.sort(
        (a, b) =>
          b.defaultAffiliateCommissionPercent -
          a.defaultAffiliateCommissionPercent,
      );
    } else if (sortBy === "price") {
      result = result.sort((a, b) => b.price - a.price);
    }
    return result;
  }, [allItems, search, minCommission, sortBy]);

  // Chips de categoria vêm do universo estável do backend (`availableCategories`),
  // NÃO dos `items` filtrados — caso contrário, selecionar uma categoria faria
  // as outras sumirem do rail. Limitamos a 12 para evitar overflow visual.
  const categoryChips = useMemo(() => {
    return (data?.availableCategories ?? []).slice(0, 12);
  }, [data?.availableCategories]);

  // KPIs do hero — calculados sobre a página atual (não totais do tenant).
  // Vale como leitura instantânea de oportunidade no que estou vendo agora.
  const heroStats = useMemo(() => {
    const avgCommission = allItems.length
      ? allItems.reduce(
          (s, p) => s + p.defaultAffiliateCommissionPercent,
          0,
        ) / allItems.length
      : 0;
    const openCount = allItems.filter(
      (p) => p.affiliationMode === "OPEN",
    ).length;
    return {
      total: data?.totalCount ?? allItems.length,
      avgCommission,
      openCount,
    };
  }, [allItems, data?.totalCount]);

  const totalCount = data?.totalCount ?? 0;
  const totalPages = Math.max(1, Math.ceil(totalCount / PAGE_SIZE));
  const hasActiveFilters = !!(
    search ||
    selectedCategories.length > 0 ||
    mode ||
    minCommission
  );
  const hasAnyData = allItems.length > 0;

  function clearAllFilters() {
    setSearch("");
    setSelectedCategories([]);
    setMode("");
    setMinCommission("");
    setPage(1);
  }

  return (
    <div className="space-y-6">
      <PageHeader
        size="hero"
        title="Marketplace de afiliação"
        subtitle="Catálogo de produtos de outros produtores abertos para promoção — solicite afiliação e ganhe comissão a cada venda."
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
            <path d="M3 9l1-5h16l1 5" />
            <path d="M5 9v11a1 1 0 0 0 1 1h12a1 1 0 0 0 1-1V9" />
            <line x1="9" y1="14" x2="15" y2="14" />
          </svg>
        }
      />

      {/* KPI ribbon — sai do hero (que ficou compacto) para uma faixa inline
          de chips brancos. Mantém a leitura instantânea de oportunidade sem
          comer espaço vertical do fold. */}
      {hasAnyData && (
        <div className="flex flex-wrap items-center gap-2">
          <StatChip
            label="Disponíveis"
            value={`${heroStats.total} ${heroStats.total === 1 ? "produto" : "produtos"}`}
          />
          <StatChip
            label="Comissão média"
            value={`${heroStats.avgCommission.toFixed(1)}%`}
          />
          <StatChip
            label="Aprovação automática"
            value={`${heroStats.openCount}`}
            tone={heroStats.openCount > 0 ? "success" : "default"}
          />
        </div>
      )}

      {/* Category chip rail — derivada do universo estável (availableCategories).
          Chips são toggles multi-select; "Todos" limpa a seleção. flex-wrap
          quebra naturalmente em múltiplas linhas quando o viewport é estreito,
          sem usar overflow-x-auto (que implicitamente seta overflow-y:hidden
          e corta os rings dos chips). Contador "X resultados" alinhado à
          direita. */}
      {hasAnyData && categoryChips.length > 0 && (
        <div className="flex items-start justify-between gap-3 flex-wrap">
          <div className="flex items-center gap-2 flex-wrap min-w-0">
            <CategoryChip
              label="Todos"
              count={data?.totalCount ?? allItems.length}
              active={selectedCategories.length === 0}
              onClick={() => {
                setSelectedCategories([]);
                setPage(1);
              }}
            />
            {categoryChips.map((c) => {
              const isActive = selectedCategories.some(
                (s) => s.toLowerCase() === c.name.toLowerCase(),
              );
              return (
                <CategoryChip
                  key={c.name}
                  label={c.name}
                  count={c.count}
                  active={isActive}
                  onClick={() => {
                    setSelectedCategories((prev) =>
                      isActive
                        ? prev.filter(
                            (s) =>
                              s.toLowerCase() !== c.name.toLowerCase(),
                          )
                        : [...prev, c.name],
                    );
                    setPage(1);
                  }}
                />
              );
            })}
          </div>
          {!isLoading && (
            <p className="shrink-0 text-xs text-gray-500 dark:text-gray-400 tabular-nums hidden sm:block mt-1.5">
              <span className="font-semibold text-gray-700 dark:text-gray-300">
                {items.length}
              </span>{" "}
              {items.length === 1 ? "resultado" : "resultados"}
              {totalCount !== items.length && (
                <span className="text-gray-400 dark:text-gray-500">
                  {" "}
                  / {totalCount}
                </span>
              )}
            </p>
          )}
        </div>
      )}

      {/* Toolbar consolidada em card único — search dominante, sort à direita,
          filtros avançados em <details> para não poluir. */}
      <div className="rounded-2xl border border-gray-200/80 bg-white dark:border-gray-800 dark:bg-gray-900 p-3 sm:p-4 space-y-3">
        <div className="flex flex-col sm:flex-row items-stretch sm:items-center gap-2">
          <div className="relative flex-1 min-w-0">
            <svg
              width="16"
              height="16"
              viewBox="0 0 24 24"
              fill="none"
              stroke="currentColor"
              strokeWidth="2"
              strokeLinecap="round"
              strokeLinejoin="round"
              className="absolute left-3 top-1/2 -translate-y-1/2 text-gray-400"
              aria-hidden="true"
            >
              <circle cx="11" cy="11" r="8" />
              <path d="m21 21-4.3-4.3" />
            </svg>
            <input
              value={search}
              onChange={(e) => {
                setSearch(e.target.value);
                setPage(1);
              }}
              placeholder="Buscar por nome, descrição ou produtor..."
              className="h-10 w-full pl-10 pr-3 rounded-xl border border-gray-200 dark:border-gray-700 bg-gray-50/60 dark:bg-gray-800/60 text-sm focus:bg-white dark:focus:bg-gray-900 focus:border-brand-400 focus:ring-2 focus:ring-brand-100 dark:focus:ring-brand-500/20 transition-all"
            />
          </div>
          <div className="flex items-center gap-2 sm:shrink-0">
            <Select
              ariaLabel="Ordenar por"
              className="w-44"
              value={sortBy}
              onChange={(v) => setSortBy(v as SortOption)}
              options={[
                { value: "newest", label: "Mais recentes" },
                { value: "commission", label: "Maior comissão" },
                { value: "price", label: "Maior preço" },
              ]}
            />
          </div>
        </div>

        {/* Linha de filtros avançados — abre/fecha sem ocupar espaço quando
            o seller não precisa. */}
        <details className="group">
          <summary className="cursor-pointer list-none inline-flex items-center gap-1.5 text-xs font-medium text-gray-600 dark:text-gray-400 hover:text-brand-600 dark:hover:text-brand-400 select-none">
            <svg
              width="14"
              height="14"
              viewBox="0 0 24 24"
              fill="none"
              stroke="currentColor"
              strokeWidth="2"
              strokeLinecap="round"
              strokeLinejoin="round"
              className="transition-transform group-open:rotate-90"
              aria-hidden="true"
            >
              <polyline points="9 18 15 12 9 6" />
            </svg>
            Filtros avançados
            {(mode || minCommission) && (
              <span className="ml-1 inline-flex items-center justify-center min-w-[18px] h-[18px] px-1 rounded-full bg-brand-500 text-white text-[10px] font-bold">
                {[mode, minCommission].filter(Boolean).length}
              </span>
            )}
          </summary>
          <div className="mt-3 grid grid-cols-1 sm:grid-cols-2 lg:grid-cols-3 gap-3">
            <FilterField label="Modo de afiliação">
              <Select
                ariaLabel="Modo de afiliação"
                value={mode}
                onChange={(v) => {
                  setMode(v as AffiliationModeCode | "");
                  setPage(1);
                }}
                options={[
                  { value: "", label: "Qualquer modo" },
                  { value: "OPEN", label: "Aberta (aprovação automática)" },
                  { value: "REQUEST", label: "Sob pedido do produtor" },
                ]}
              />
            </FilterField>
            <FilterField
              label="Comissão mínima"
              hint="Filtra apenas produtos com comissão igual ou maior."
            >
              <div className="relative">
                <input
                  type="number"
                  step="0.1"
                  min="0"
                  max="100"
                  value={minCommission}
                  onChange={(e) => setMinCommission(e.target.value)}
                  placeholder="Ex: 30"
                  className="h-10 w-full rounded-xl border border-gray-200 dark:border-gray-700 bg-white dark:bg-gray-900 pl-3 pr-8 text-sm tabular-nums focus:border-brand-400 focus:ring-2 focus:ring-brand-100 dark:focus:ring-brand-500/20 transition-all"
                />
                <span className="absolute right-3 top-1/2 -translate-y-1/2 text-xs text-gray-400 pointer-events-none">
                  %
                </span>
              </div>
            </FilterField>
          </div>
        </details>

        {/* Active filters como chips clicáveis para remover. Aparece apenas
            quando algo está aplicado — mantém a toolbar limpa no estado default. */}
        {hasActiveFilters && (
          <div className="flex items-center gap-2 flex-wrap pt-2 border-t border-gray-100 dark:border-gray-800">
            <span className="text-[11px] font-medium text-gray-500 dark:text-gray-400">
              Filtros ativos:
            </span>
            {search && (
              <FilterChip
                label={`"${search}"`}
                onRemove={() => setSearch("")}
              />
            )}
            {selectedCategories.map((c) => (
              <FilterChip
                key={c}
                label={`Categoria: ${c}`}
                onRemove={() =>
                  setSelectedCategories((prev) => prev.filter((s) => s !== c))
                }
              />
            ))}
            {mode && (
              <FilterChip
                label={`Modo: ${mode === "OPEN" ? "Aberta" : "Sob aprovação"}`}
                onRemove={() => setMode("")}
              />
            )}
            {minCommission && (
              <FilterChip
                label={`Comissão ≥ ${minCommission}%`}
                onRemove={() => setMinCommission("")}
              />
            )}
            <button
              onClick={clearAllFilters}
              className="text-[11px] font-medium text-brand-600 dark:text-brand-400 hover:underline ml-1"
            >
              Limpar todos
            </button>
          </div>
        )}
      </div>

      {isLoading ? (
        <GridSkeleton />
      ) : items.length === 0 ? (
        hasActiveFilters ? (
          <EmptyFilterState onClear={clearAllFilters} />
        ) : (
          <EmptyChecklistState
            title="O catálogo está vazio por enquanto"
            subtitle="Quando produtores no seu tenant abrirem afiliação, os produtos aparecem aqui."
            steps={[
              {
                icon: "🛍",
                title: "Você também é produtor?",
                description:
                  "Crie um produto seu para começar a vender ou para entender a experiência.",
                href: "/products/new",
                cta: "Criar produto",
              },
              {
                icon: "⏰",
                title: "Volte mais tarde",
                description:
                  "O catálogo atualiza em tempo real conforme novos produtos são publicados.",
              },
            ]}
          />
        )
      ) : (
        <>
          <div className="grid grid-cols-1 lg:grid-cols-2 2xl:grid-cols-3 gap-3">
            {items.map((p) => (
              <ProductCard
                key={p.id}
                product={p}
                onViewDetails={() => setDetailProduct(p)}
              />
            ))}
          </div>
          {totalPages > 1 && (
            <div className="flex items-center justify-between pt-2">
              <button
                onClick={() => setPage(Math.max(1, page - 1))}
                disabled={page === 1}
                className="h-10 inline-flex items-center gap-1.5 rounded-xl border border-gray-200 dark:border-gray-700 bg-white dark:bg-gray-900 px-4 text-sm font-medium text-gray-700 dark:text-gray-300 hover:bg-gray-50 dark:hover:bg-gray-800 disabled:opacity-40 disabled:hover:bg-white dark:disabled:hover:bg-gray-900 transition-colors"
              >
                <svg
                  width="14"
                  height="14"
                  viewBox="0 0 24 24"
                  fill="none"
                  stroke="currentColor"
                  strokeWidth="2"
                  strokeLinecap="round"
                  strokeLinejoin="round"
                  aria-hidden="true"
                >
                  <polyline points="15 18 9 12 15 6" />
                </svg>
                Anterior
              </button>
              <span className="text-sm text-gray-500 dark:text-gray-400 tabular-nums">
                Página{" "}
                <span className="font-semibold text-gray-700 dark:text-gray-300">
                  {page}
                </span>{" "}
                de{" "}
                <span className="font-semibold text-gray-700 dark:text-gray-300">
                  {totalPages}
                </span>
              </span>
              <button
                onClick={() => setPage(Math.min(totalPages, page + 1))}
                disabled={page === totalPages}
                className="h-10 inline-flex items-center gap-1.5 rounded-xl border border-gray-200 dark:border-gray-700 bg-white dark:bg-gray-900 px-4 text-sm font-medium text-gray-700 dark:text-gray-300 hover:bg-gray-50 dark:hover:bg-gray-800 disabled:opacity-40 disabled:hover:bg-white dark:disabled:hover:bg-gray-900 transition-colors"
              >
                Próxima
                <svg
                  width="14"
                  height="14"
                  viewBox="0 0 24 24"
                  fill="none"
                  stroke="currentColor"
                  strokeWidth="2"
                  strokeLinecap="round"
                  strokeLinejoin="round"
                  aria-hidden="true"
                >
                  <polyline points="9 18 15 12 9 6" />
                </svg>
              </button>
            </div>
          )}
        </>
      )}

      {/* Modal "Ver detalhes" — único compartilhado, abre via setDetailProduct
          a partir de qualquer card. Centraliza foco e fácil de portar para
          rota /affiliate-marketplace/[id] no futuro se precisar de SEO. */}
      <ProductDetailModal
        key={detailProduct?.id ?? "closed"}
        product={detailProduct}
        onClose={() => setDetailProduct(null)}
      />
    </div>
  );
}

/* ------------------------------- Subcomponents ------------------------------ */

/**
 * Rodapé alternativo do ProductCard quando já existe uma afiliação registrada
 * para o caller — substitui o CTA "Afiliar" por um badge informativo +
 * "Ver detalhes" (que abre o modal). Evita o erro "afiliação já existe".
 */
function AffiliationStatusFooter({
  status,
  onViewDetails,
}: {
  status: AffiliationStatusCode;
  onViewDetails: () => void;
}) {
  const config = AFFILIATION_STATUS_FOOTER[status];
  const badgeContent = (
    <>
      <span className="inline-flex items-center justify-center w-4 h-4 rounded-full shrink-0">
        {config.icon}
      </span>
      <span className="min-w-0 truncate">{config.label}</span>
    </>
  );

  return (
    <div className="flex items-center justify-between gap-2">
      {config.linkHref ? (
        <Link
          href={config.linkHref}
          onClick={(e) => e.stopPropagation()}
          className={`${config.cls} hover:opacity-90 transition-opacity`}
        >
          {badgeContent}
        </Link>
      ) : (
        <div className={config.cls}>{badgeContent}</div>
      )}
      <button
        onClick={(e) => {
          e.stopPropagation();
          onViewDetails();
        }}
        className="shrink-0 h-8 inline-flex items-center justify-center rounded-lg px-2.5 text-[11px] font-semibold text-gray-600 hover:text-brand-600 hover:bg-brand-50 dark:text-gray-400 dark:hover:text-brand-300 dark:hover:bg-brand-500/10 transition-colors"
      >
        Ver detalhes
      </button>
    </div>
  );
}

const AFFILIATION_STATUS_FOOTER: Record<
  AffiliationStatusCode,
  {
    label: string;
    cls: string;
    icon: React.ReactNode;
    linkHref?: string;
  }
> = {
  PENDING: {
    label: "Aguardando aprovação",
    cls: "inline-flex items-center gap-1.5 rounded-lg bg-warning-50 dark:bg-warning-500/10 ring-1 ring-warning-200/60 dark:ring-warning-500/20 px-2.5 py-1.5 text-[11px] font-semibold text-warning-700 dark:text-warning-300 min-w-0",
    icon: (
      <svg
        width="11"
        height="11"
        viewBox="0 0 24 24"
        fill="none"
        stroke="currentColor"
        strokeWidth="2"
        strokeLinecap="round"
        strokeLinejoin="round"
        aria-hidden="true"
      >
        <circle cx="12" cy="12" r="10" />
        <polyline points="12 6 12 12 16 14" />
      </svg>
    ),
  },
  APPROVED: {
    label: "Afiliado · Ver link",
    cls: "inline-flex items-center gap-1.5 rounded-lg bg-success-50 dark:bg-success-500/10 ring-1 ring-success-200/60 dark:ring-success-500/20 px-2.5 py-1.5 text-[11px] font-semibold text-success-700 dark:text-success-300 min-w-0",
    icon: (
      <svg
        width="11"
        height="11"
        viewBox="0 0 24 24"
        fill="none"
        stroke="currentColor"
        strokeWidth="2.5"
        strokeLinecap="round"
        strokeLinejoin="round"
        aria-hidden="true"
      >
        <polyline points="20 6 9 17 4 12" />
      </svg>
    ),
    linkHref: "/affiliations",
  },
  REJECTED: {
    label: "Solicitação recusada",
    cls: "inline-flex items-center gap-1.5 rounded-lg bg-gray-100 dark:bg-gray-800 ring-1 ring-gray-200/60 dark:ring-gray-700/60 px-2.5 py-1.5 text-[11px] font-semibold text-gray-600 dark:text-gray-400 min-w-0",
    icon: (
      <svg
        width="11"
        height="11"
        viewBox="0 0 24 24"
        fill="none"
        stroke="currentColor"
        strokeWidth="2.5"
        strokeLinecap="round"
        aria-hidden="true"
      >
        <line x1="18" y1="6" x2="6" y2="18" />
        <line x1="6" y1="6" x2="18" y2="18" />
      </svg>
    ),
  },
  REVOKED: {
    label: "Afiliação revogada",
    cls: "inline-flex items-center gap-1.5 rounded-lg bg-gray-100 dark:bg-gray-800 ring-1 ring-gray-200/60 dark:ring-gray-700/60 px-2.5 py-1.5 text-[11px] font-semibold text-gray-600 dark:text-gray-400 min-w-0",
    icon: (
      <svg
        width="11"
        height="11"
        viewBox="0 0 24 24"
        fill="none"
        stroke="currentColor"
        strokeWidth="2.5"
        strokeLinecap="round"
        strokeLinejoin="round"
        aria-hidden="true"
      >
        <path d="M3 6h18" />
        <path d="M19 6v14a2 2 0 0 1-2 2H7a2 2 0 0 1-2-2V6" />
      </svg>
    ),
  },
};

/**
 * Callout grande para usar no ProductDetailModal quando já existe afiliação
 * registrada — substitui a seção "Como funciona". Apresenta o estado em
 * linguagem clara + ação contextual (ex: APPROVED tem link para o produto).
 */
function ExistingAffiliationCallout({
  status,
  onClose,
}: {
  status: AffiliationStatusCode;
  onClose: () => void;
}) {
  const map: Record<
    AffiliationStatusCode,
    {
      bg: string;
      ring: string;
      text: string;
      icon: React.ReactNode;
      title: string;
      body: React.ReactNode;
    }
  > = {
    PENDING: {
      bg: "bg-warning-50 dark:bg-warning-500/10",
      ring: "ring-warning-200/60 dark:ring-warning-500/20",
      text: "text-warning-700 dark:text-warning-300",
      icon: (
        <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true">
          <circle cx="12" cy="12" r="10" />
          <polyline points="12 6 12 12 16 14" />
        </svg>
      ),
      title: "Aguardando aprovação do produtor",
      body: (
        <>
          Sua solicitação foi enviada e está na fila do produtor. Você será
          notificado quando ele aprovar ou recusar.
        </>
      ),
    },
    APPROVED: {
      bg: "bg-success-50 dark:bg-success-500/10",
      ring: "ring-success-200/60 dark:ring-success-500/20",
      text: "text-success-700 dark:text-success-300",
      icon: (
        <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.5" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true">
          <polyline points="20 6 9 17 4 12" />
        </svg>
      ),
      title: "Você já está afiliado a este produto",
      body: (
        <>
          Sua afiliação está ativa e o link de tracking está disponível em{" "}
          <Link
            href="/affiliations"
            onClick={onClose}
            className="font-semibold underline underline-offset-2"
          >
            Minhas afiliações
          </Link>
          .
        </>
      ),
    },
    REJECTED: {
      bg: "bg-gray-100 dark:bg-gray-800",
      ring: "ring-gray-200/60 dark:ring-gray-700/60",
      text: "text-gray-700 dark:text-gray-300",
      icon: (
        <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.5" strokeLinecap="round" aria-hidden="true">
          <line x1="18" y1="6" x2="6" y2="18" />
          <line x1="6" y1="6" x2="18" y2="18" />
        </svg>
      ),
      title: "Solicitação recusada pelo produtor",
      body: (
        <>
          Sua solicitação anterior para este produto foi recusada. Procure
          outros produtos no catálogo que combinem com seu público.
        </>
      ),
    },
    REVOKED: {
      bg: "bg-gray-100 dark:bg-gray-800",
      ring: "ring-gray-200/60 dark:ring-gray-700/60",
      text: "text-gray-700 dark:text-gray-300",
      icon: (
        <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true">
          <path d="M3 6h18" />
          <path d="M19 6v14a2 2 0 0 1-2 2H7a2 2 0 0 1-2-2V6" />
        </svg>
      ),
      title: "Afiliação revogada",
      body: (
        <>
          O produtor revogou sua afiliação a este produto. Entre em contato
          diretamente com ele para entender o motivo.
        </>
      ),
    },
  };

  const cfg = map[status];
  return (
    <div className={`rounded-xl px-4 py-3 ring-1 ${cfg.bg} ${cfg.ring}`}>
      <div className={`flex items-start gap-2.5 ${cfg.text}`}>
        <span className="shrink-0 mt-0.5">{cfg.icon}</span>
        <div className="min-w-0">
          <p className="text-sm font-semibold leading-tight">{cfg.title}</p>
          <p className={`text-xs leading-relaxed mt-1 ${cfg.text} opacity-90`}>
            {cfg.body}
          </p>
        </div>
      </div>
    </div>
  );
}

function StatChip({
  label,
  value,
  tone = "default",
}: {
  label: string;
  value: string;
  tone?: "default" | "success";
}) {
  const toneCls =
    tone === "success"
      ? "ring-success-200/70 dark:ring-success-500/30"
      : "ring-gray-200/80 dark:ring-gray-700/60";
  const valueCls =
    tone === "success"
      ? "text-success-700 dark:text-success-300"
      : "text-gray-900 dark:text-white";
  return (
    <span
      className={`inline-flex items-center gap-1.5 rounded-full bg-white dark:bg-gray-900 px-3 py-1 ring-1 ${toneCls}`}
    >
      <span className="text-[10px] font-semibold uppercase tracking-wider text-gray-500 dark:text-gray-400">
        {label}
      </span>
      <span className={`text-xs font-bold tabular-nums ${valueCls}`}>
        {value}
      </span>
    </span>
  );
}

function CategoryChip({
  label,
  count,
  active,
  onClick,
}: {
  label: string;
  count: number;
  active: boolean;
  onClick: () => void;
}) {
  return (
    <button
      onClick={onClick}
      className={`h-9 inline-flex items-center gap-2 rounded-full px-4 text-xs font-semibold whitespace-nowrap transition-all ring-1 ${
        active
          ? "bg-brand-500 text-white ring-brand-500 shadow-sm shadow-brand-500/20"
          : "bg-white dark:bg-gray-900 text-gray-700 dark:text-gray-300 ring-gray-200 dark:ring-gray-700 hover:ring-brand-300 hover:text-brand-700 dark:hover:text-brand-300"
      }`}
    >
      <span>{label}</span>
      <span
        className={`tabular-nums text-[10px] font-bold px-1.5 py-0.5 rounded-full ${
          active
            ? "bg-white/20 text-white"
            : "bg-gray-100 dark:bg-gray-800 text-gray-500 dark:text-gray-400"
        }`}
      >
        {count}
      </span>
    </button>
  );
}

function FilterField({
  label,
  hint,
  children,
}: {
  label: string;
  hint?: string;
  children: React.ReactNode;
}) {
  return (
    <div>
      <label className="block text-[11px] font-semibold uppercase tracking-wider text-gray-500 dark:text-gray-400 mb-1.5">
        {label}
      </label>
      {children}
      {hint && (
        <p className="text-[10px] text-gray-400 dark:text-gray-500 mt-1">
          {hint}
        </p>
      )}
    </div>
  );
}

function FilterChip({
  label,
  onRemove,
}: {
  label: string;
  onRemove: () => void;
}) {
  return (
    <button
      onClick={onRemove}
      className="inline-flex items-center gap-1.5 pl-2.5 pr-1.5 py-1 rounded-full text-[11px] font-medium bg-brand-50 text-brand-700 ring-1 ring-brand-200/60 dark:bg-brand-500/15 dark:text-brand-300 dark:ring-brand-500/30 hover:bg-brand-100 dark:hover:bg-brand-500/25 transition-colors"
    >
      <span>{label}</span>
      <span className="inline-flex items-center justify-center w-4 h-4 rounded-full bg-brand-200/60 dark:bg-brand-500/30">
        <svg
          width="8"
          height="8"
          viewBox="0 0 24 24"
          fill="none"
          stroke="currentColor"
          strokeWidth="3"
          strokeLinecap="round"
          aria-hidden="true"
        >
          <line x1="18" y1="6" x2="6" y2="18" />
          <line x1="6" y1="6" x2="18" y2="18" />
        </svg>
      </span>
    </button>
  );
}

function EmptyFilterState({ onClear }: { onClear: () => void }) {
  return (
    <div className="rounded-2xl border border-gray-200/80 bg-white dark:border-gray-800 dark:bg-gray-900 p-10 text-center">
      <Illustration name="no-results" size="lg" className="mx-auto mb-3" />
      <p className="text-sm font-semibold text-gray-800 dark:text-gray-200">
        Nenhum produto atende aos filtros
      </p>
      <p className="text-xs text-gray-500 dark:text-gray-400 mt-1 mb-5 max-w-sm mx-auto">
        Tente afrouxar os critérios ou remover a busca por nome para ver mais
        resultados.
      </p>
      <button
        onClick={onClear}
        className="h-10 inline-flex items-center rounded-xl bg-brand-500 hover:bg-brand-600 px-5 text-sm font-semibold text-white shadow-sm shadow-brand-500/20 transition-colors"
      >
        Limpar filtros
      </button>
    </div>
  );
}

/* ------------------------------- ProductCard ------------------------------- */

function ProductCard({
  product,
  onViewDetails,
}: {
  product: Product;
  onViewDetails: () => void;
}) {
  const queryClient = useQueryClient();
  const [error, setError] = useState<string | null>(null);
  const [successId, setSuccessId] = useState<string | null>(null);

  const request = useMutation({
    mutationFn: () => marketplaceService.requestAffiliation(product.id),
    onSuccess: (aff) => {
      setSuccessId(aff.status === "APPROVED" ? "approved" : "pending");
      queryClient.invalidateQueries({ queryKey: ["affiliations"] });
      queryClient.invalidateQueries({ queryKey: ["affiliate-marketplace"] });
    },
    onError: (err) =>
      setError(err instanceof ApiError ? err.message : "Erro ao solicitar."),
  });

  const badge = MODE_BADGE[product.affiliationMode];
  const earningsPerSale =
    (product.price * product.defaultAffiliateCommissionPercent) / 100;
  const isHighCommission =
    product.defaultAffiliateCommissionPercent >= HIGH_COMMISSION_THRESHOLD;

  // Status efetivo de afiliação: prioriza ação recente desta sessão (successId)
  // sobre o estado vindo do backend (mais novo > mais velho). Drives o rodapé.
  const effectiveStatus: AffiliationStatusCode | null =
    successId === "approved"
      ? "APPROVED"
      : successId === "pending"
        ? "PENDING"
        : (product.currentSellerAffiliationStatus ?? null);

  return (
    // Card inteiro é clicável (abre o modal de detalhes). Botões internos
    // (Afiliar, Ver detalhes, etc.) usam stopPropagation para não disparar
    // o onClick do card. Role/tabIndex/keyboard listener garantem acessibilidade.
    <div
      role="button"
      tabIndex={0}
      onClick={onViewDetails}
      onKeyDown={(e) => {
        if (e.key === "Enter" || e.key === " ") {
          e.preventDefault();
          onViewDetails();
        }
      }}
      className="group rounded-2xl border border-gray-200/80 dark:border-gray-800 bg-white dark:bg-gray-900 overflow-hidden flex transition-all duration-200 hover:border-brand-300 dark:hover:border-brand-500/40 hover:shadow-[0_8px_30px_-12px_rgba(176,38,255,0.25)] hover:-translate-y-0.5 cursor-pointer focus:outline-none focus-visible:ring-2 focus-visible:ring-brand-400 focus-visible:ring-offset-2 dark:focus-visible:ring-offset-gray-950"
    >
      {/* Cover quadrado à esquerda. Largura fixa em desktop para preservar
          a proporção landscape do card; altura segue o conteúdo (self-stretch). */}
      <div className="relative shrink-0 w-32 sm:w-36 bg-gradient-to-br from-gray-100 to-gray-200 dark:from-gray-800 dark:to-gray-900 flex items-center justify-center text-gray-400 overflow-hidden">
        {product.coverImageUrl ? (
          // eslint-disable-next-line @next/next/no-img-element
          <img
            src={product.coverImageUrl}
            alt={product.name}
            className="absolute inset-0 w-full h-full object-cover transition-transform duration-500 group-hover:scale-105"
          />
        ) : (
          <svg
            width="32"
            height="32"
            viewBox="0 0 24 24"
            fill="none"
            stroke="currentColor"
            strokeWidth="1.5"
            strokeLinecap="round"
            strokeLinejoin="round"
            aria-hidden="true"
          >
            <rect x="3" y="3" width="18" height="18" rx="2" />
            <circle cx="9" cy="9" r="2" />
            <path d="m21 15-3.086-3.086a2 2 0 0 0-2.828 0L6 21" />
          </svg>
        )}

        {/* Gradient overlay sutil no topo do cover — melhora contraste para
            futuros badges sem escurecer a imagem inteira. */}
        <div className="pointer-events-none absolute inset-x-0 top-0 h-1/2 bg-gradient-to-b from-black/10 to-transparent" />
      </div>

      {/* Pane direito — conteúdo + CTA, flex column. Min-w-0 garante que
          o truncate funcione com conteúdo longo. */}
      <div className="flex-1 min-w-0 p-3 flex flex-col">
        {/* Linha superior: apenas o modo de afiliação (categoria já existe
            no chip rail acima — não duplicar). */}
        <div className="flex items-center gap-1.5 mb-1.5">
          <span
            className={`inline-flex items-center gap-1 px-1.5 py-0.5 rounded text-[9px] uppercase tracking-wider font-bold ring-1 ${badge.cls}`}
          >
            <span className={`w-1 h-1 rounded-full ${badge.dot}`} />
            {badge.label}
          </span>
        </div>

        {/* Título — 1 linha apenas, truncate. */}
        <h3 className="text-[13px] font-semibold text-gray-900 dark:text-white leading-snug mb-0.5 line-clamp-1">
          {product.name}
        </h3>

        {/* Produtor inline. */}
        <div className="flex items-center gap-1 mb-1.5">
          <span className="inline-flex items-center justify-center w-3.5 h-3.5 rounded-full bg-gradient-to-br from-brand-100 to-brand-200 dark:from-brand-500/30 dark:to-brand-500/10 text-[7px] font-bold text-brand-700 dark:text-brand-300 ring-1 ring-white/60 dark:ring-gray-800">
            {initialsOf(product.ownerSellerName)}
          </span>
          <span className="text-[10px] text-gray-500 dark:text-gray-400 truncate">
            por{" "}
            <span className="font-medium text-gray-700 dark:text-gray-300">
              {product.ownerSellerName ?? "Produtor desconhecido"}
            </span>
          </span>
        </div>

        {/* Descrição em 1 linha — restaurada para devolver identidade ao
            produto. line-clamp-1 garante que não cresça o card. */}
        {product.description && (
          <p className="text-[10px] text-gray-500 dark:text-gray-400 leading-relaxed line-clamp-1 mb-2">
            {product.description}
          </p>
        )}

        {/* Banner de comissão — formato horizontal compacto. R$, %, e quando
            aplicável o selo "Alta comissão" dourado tudo na mesma linha. */}
        <div className="rounded-lg bg-gradient-to-br from-brand-50 to-brand-100/60 dark:from-brand-500/15 dark:to-brand-500/5 ring-1 ring-brand-200/60 dark:ring-brand-500/20 px-2.5 py-1.5 mb-2">
          <div className="flex items-center justify-between gap-1.5 mb-0.5">
            <p className="text-[8px] font-semibold uppercase tracking-wider text-brand-700/80 dark:text-brand-300/80 leading-none">
              Você ganha por venda
            </p>
            {isHighCommission && (
              <span className="inline-flex items-center gap-0.5 px-1.5 py-0.5 rounded text-[8px] uppercase tracking-wider font-bold bg-gradient-to-br from-amber-400 to-amber-500 text-amber-950 shadow-sm shadow-amber-500/30 leading-none">
                <svg
                  width="8"
                  height="8"
                  viewBox="0 0 24 24"
                  fill="currentColor"
                  aria-hidden="true"
                >
                  <path d="M12 2 15.09 8.26 22 9.27l-5 4.87 1.18 6.88L12 17.77l-6.18 3.25L7 14.14 2 9.27l6.91-1.01z" />
                </svg>
                Alta comissão
              </span>
            )}
          </div>
          <div className="flex items-baseline gap-1.5">
            <span className="text-sm font-bold text-brand-700 dark:text-brand-300 tabular-nums leading-tight">
              {formatBRL(earningsPerSale)}
            </span>
            <span className="text-[10px] font-bold text-brand-700/70 dark:text-brand-300/70 tabular-nums">
              ({product.defaultAffiliateCommissionPercent.toFixed(1)}%)
            </span>
          </div>
        </div>

        {/* Rodapé inline: estado dirigido por effectiveStatus.
            - null → "Cliente paga + Ver detalhes + Afiliar →"
            - PENDING/APPROVED/REJECTED/REVOKED → badge informativo + Ver detalhes
            (sem CTA "Afiliar", já tem afiliação registrada). */}
        <div className="mt-auto">
          {error && (
            <div className="rounded-lg bg-error-50 dark:bg-error-500/10 ring-1 ring-error-200/60 dark:ring-error-500/20 px-2 py-1.5 text-[10px] text-error-700 dark:text-error-300 mb-1.5">
              {error}
            </div>
          )}
          {effectiveStatus === null ? (
            <div className="flex items-center justify-between gap-2">
              <div className="min-w-0 flex flex-col">
                <span className="text-[9px] uppercase tracking-wider text-gray-400 dark:text-gray-500 leading-none">
                  Cliente paga
                </span>
                <span className="text-[12px] font-semibold text-gray-700 dark:text-gray-300 tabular-nums leading-tight mt-0.5">
                  {formatBRL(product.price)}
                </span>
              </div>
              <div className="flex items-center gap-1.5 shrink-0">
                <button
                  onClick={(e) => {
                    e.stopPropagation();
                    onViewDetails();
                  }}
                  className="h-8 inline-flex items-center justify-center rounded-lg px-2.5 text-[11px] font-semibold text-gray-600 hover:text-brand-600 hover:bg-brand-50 dark:text-gray-400 dark:hover:text-brand-300 dark:hover:bg-brand-500/10 transition-colors"
                >
                  Ver detalhes
                </button>
                <button
                  onClick={(e) => {
                    e.stopPropagation();
                    setError(null);
                    request.mutate();
                  }}
                  disabled={request.isPending}
                  className="h-8 inline-flex items-center justify-center gap-1 rounded-lg bg-brand-500 hover:bg-brand-600 px-3 text-[11px] font-semibold text-white shadow-sm shadow-brand-500/20 transition-all hover:shadow-md hover:shadow-brand-500/25 disabled:opacity-50 disabled:hover:shadow-sm"
                >
                  {request.isPending ? (
                    "Enviando..."
                  ) : (
                    <>
                      Afiliar
                      <svg
                        width="12"
                        height="12"
                        viewBox="0 0 24 24"
                        fill="none"
                        stroke="currentColor"
                        strokeWidth="2.5"
                        strokeLinecap="round"
                        strokeLinejoin="round"
                        className="transition-transform group-hover:translate-x-0.5"
                        aria-hidden="true"
                      >
                        <line x1="5" y1="12" x2="19" y2="12" />
                        <polyline points="12 5 19 12 12 19" />
                      </svg>
                    </>
                  )}
                </button>
              </div>
            </div>
          ) : (
            <AffiliationStatusFooter
              status={effectiveStatus}
              onViewDetails={onViewDetails}
            />
          )}
        </div>
      </div>
    </div>
  );
}

function GridSkeleton() {
  return (
    <div className="grid grid-cols-1 lg:grid-cols-2 2xl:grid-cols-3 gap-3">
      {[0, 1, 2, 3, 4, 5].map((i) => (
        <div
          key={i}
          className="rounded-2xl border border-gray-200/80 dark:border-gray-800 bg-white dark:bg-gray-900 overflow-hidden animate-pulse flex"
        >
          <div className="shrink-0 w-32 sm:w-36 bg-gradient-to-br from-gray-200 to-gray-100 dark:from-gray-800 dark:to-gray-900" />
          <div className="flex-1 p-3 space-y-2">
            <div className="flex items-center gap-1.5">
              <div className="h-3 w-12 bg-gray-200 dark:bg-gray-700 rounded" />
              <div className="h-3 w-16 bg-gray-200 dark:bg-gray-700 rounded" />
            </div>
            <div className="h-3.5 w-3/4 bg-gray-200 dark:bg-gray-700 rounded" />
            <div className="flex items-center gap-1">
              <div className="w-3.5 h-3.5 rounded-full bg-gray-200 dark:bg-gray-700" />
              <div className="h-2.5 w-1/3 bg-gray-100 dark:bg-gray-800 rounded" />
            </div>
            <div className="h-10 w-full bg-brand-50/60 dark:bg-brand-500/5 rounded-lg" />
            <div className="flex items-center justify-between gap-2 pt-1">
              <div className="h-6 w-20 bg-gray-100 dark:bg-gray-800 rounded" />
              <div className="h-8 w-32 bg-gray-200 dark:bg-gray-700 rounded-lg" />
            </div>
          </div>
        </div>
      ))}
    </div>
  );
}

/* ----------------------------- ProductDetailModal --------------------------- */

/**
 * Modal de detalhes do produto, aberto a partir de "Ver detalhes" em qualquer
 * card. Apresenta cover grande, descrição completa, métricas de comissão e
 * permite solicitar afiliação direto (sem fechar e clicar de novo).
 *
 * Z-index z-[100000] para sobrepor o header sticky (z-99999). Backdrop com
 * blur leve para hierarquia clara entre o modal e a página atrás.
 */
function ProductDetailModal({
  product,
  onClose,
}: {
  product: Product | null;
  onClose: () => void;
}) {
  const queryClient = useQueryClient();
  const [error, setError] = useState<string | null>(null);
  const [successId, setSuccessId] = useState<string | null>(null);

  const request = useMutation({
    mutationFn: () => marketplaceService.requestAffiliation(product!.id),
    onSuccess: (aff) => {
      setSuccessId(aff.status === "APPROVED" ? "approved" : "pending");
      queryClient.invalidateQueries({ queryKey: ["affiliations"] });
      queryClient.invalidateQueries({ queryKey: ["affiliate-marketplace"] });
    },
    onError: (err) =>
      setError(err instanceof ApiError ? err.message : "Erro ao solicitar."),
  });

  // Status efetivo — mesma lógica do card. Drives o footer/CTA do modal.
  const effectiveStatus: AffiliationStatusCode | null = !product
    ? null
    : successId === "approved"
      ? "APPROVED"
      : successId === "pending"
        ? "PENDING"
        : (product.currentSellerAffiliationStatus ?? null);

  // ESC fecha o modal. Scroll lock delegado ao hook `useScrollLock` para
  // manter consistência com os demais modais do app (idempotente, restaura
  // overflow original no cleanup).
  // Não precisamos de effect para resetar state local: o modal só renderiza
  // quando `product` é não-nulo (early-return abaixo), então React monta de
  // novo a cada abertura, garantindo state fresh por produto.
  useScrollLock(product !== null);
  useEffect(() => {
    if (!product) return;
    const onKey = (e: KeyboardEvent) => {
      if (e.key === "Escape") onClose();
    };
    document.addEventListener("keydown", onKey);
    return () => {
      document.removeEventListener("keydown", onKey);
    };
  }, [product, onClose]);

  if (!product) return null;

  const badge = MODE_BADGE[product.affiliationMode];
  const earningsPerSale =
    (product.price * product.defaultAffiliateCommissionPercent) / 100;
  const isHighCommission =
    product.defaultAffiliateCommissionPercent >= HIGH_COMMISSION_THRESHOLD;

  return (
    <div
      className="fixed inset-0 z-[100000] flex items-center justify-center p-4 sm:p-6"
      role="dialog"
      aria-modal="true"
      aria-labelledby="product-detail-title"
    >
      {/* Backdrop com fade-in suave */}
      <button
        type="button"
        aria-label="Fechar"
        onClick={onClose}
        className="absolute inset-0 bg-gray-400/5 backdrop-blur-[3px] cursor-default modal-backdrop-in"
      />

      {/* Modal card — max-w-md (28rem/~448px) é mais compacto que o anterior
          (max-w-2xl/672px) sem perder legibilidade do conteúdo. Animação
          de entrada scale + fade para sensação de "pop" suave. */}
      <div className="relative w-full max-w-md max-h-[85vh] overflow-hidden rounded-2xl bg-white dark:bg-gray-900 shadow-2xl flex flex-col modal-content-in">
        {/* Cover compacto no topo, com badges sobrepostos. */}
        <div className="relative aspect-[16/9] w-full bg-gradient-to-br from-gray-100 to-gray-200 dark:from-gray-800 dark:to-gray-900 overflow-hidden shrink-0">
          {product.coverImageUrl ? (
            // eslint-disable-next-line @next/next/no-img-element
            <img
              src={product.coverImageUrl}
              alt={product.name}
              className="w-full h-full object-cover"
            />
          ) : (
            <div className="w-full h-full flex items-center justify-center text-gray-400">
              <svg
                width="64"
                height="64"
                viewBox="0 0 24 24"
                fill="none"
                stroke="currentColor"
                strokeWidth="1.5"
                strokeLinecap="round"
                strokeLinejoin="round"
                aria-hidden="true"
              >
                <rect x="3" y="3" width="18" height="18" rx="2" />
                <circle cx="9" cy="9" r="2" />
                <path d="m21 15-3.086-3.086a2 2 0 0 0-2.828 0L6 21" />
              </svg>
            </div>
          )}
          <div className="pointer-events-none absolute inset-x-0 top-0 h-1/3 bg-gradient-to-b from-black/40 to-transparent" />
          <div className="pointer-events-none absolute inset-x-0 bottom-0 h-1/3 bg-gradient-to-t from-black/40 to-transparent" />

          {/* Botão fechar no canto superior direito. */}
          <button
            type="button"
            onClick={onClose}
            aria-label="Fechar"
            className="absolute top-3 right-3 inline-flex items-center justify-center w-8 h-8 rounded-full bg-white/90 hover:bg-white text-gray-700 backdrop-blur-md shadow-sm transition-colors"
          >
            <svg
              width="14"
              height="14"
              viewBox="0 0 24 24"
              fill="none"
              stroke="currentColor"
              strokeWidth="2.5"
              strokeLinecap="round"
              aria-hidden="true"
            >
              <line x1="18" y1="6" x2="6" y2="18" />
              <line x1="6" y1="6" x2="18" y2="18" />
            </svg>
          </button>

          {/* Badges sobrepostos no canto inferior esquerdo do cover. */}
          <div className="absolute bottom-3 left-3 flex items-center gap-1.5 flex-wrap">
            {product.category && (
              <span className="inline-flex items-center px-2 py-1 rounded-md text-[10px] uppercase tracking-wider font-semibold bg-white/90 text-gray-700 backdrop-blur-md">
                {product.category}
              </span>
            )}
            <span
              className={`inline-flex items-center gap-1 px-2 py-1 rounded-md text-[10px] uppercase tracking-wider font-bold ring-1 backdrop-blur-md ${badge.cls}`}
            >
              <span className={`w-1.5 h-1.5 rounded-full ${badge.dot}`} />
              {badge.label}
            </span>
            {isHighCommission && (
              <span className="inline-flex items-center gap-1 px-2 py-1 rounded-md text-[10px] uppercase tracking-wider font-bold bg-gradient-to-br from-amber-400 to-amber-500 text-amber-950 shadow-sm shadow-amber-500/30">
                <svg width="10" height="10" viewBox="0 0 24 24" fill="currentColor" aria-hidden="true">
                  <path d="M12 2 15.09 8.26 22 9.27l-5 4.87 1.18 6.88L12 17.77l-6.18 3.25L7 14.14 2 9.27l6.91-1.01z" />
                </svg>
                Alta comissão
              </span>
            )}
          </div>
        </div>

        {/* Conteúdo do modal — scroll vertical quando excede a altura. */}
        <div className="flex-1 overflow-y-auto p-4">
          <h2
            id="product-detail-title"
            className="text-base font-bold text-gray-900 dark:text-white leading-tight"
          >
            {product.name}
          </h2>

          {/* Linha do produtor com avatar. */}
          <div className="flex items-center gap-2 mt-1.5 mb-3">
            <span className="inline-flex items-center justify-center w-6 h-6 rounded-full bg-gradient-to-br from-brand-100 to-brand-200 dark:from-brand-500/30 dark:to-brand-500/10 text-[10px] font-bold text-brand-700 dark:text-brand-300 ring-2 ring-white dark:ring-gray-900">
              {initialsOf(product.ownerSellerName)}
            </span>
            <div className="min-w-0">
              <p className="text-[9px] uppercase tracking-wider text-gray-400 dark:text-gray-500 leading-none">
                Produtor
              </p>
              <p className="text-xs font-semibold text-gray-700 dark:text-gray-300 truncate">
                {product.ownerSellerName ?? "Produtor desconhecido"}
              </p>
            </div>
          </div>

          {/* Banner de comissão. */}
          <div className="rounded-xl bg-gradient-to-br from-brand-50 to-brand-100/60 dark:from-brand-500/15 dark:to-brand-500/5 ring-1 ring-brand-200/60 dark:ring-brand-500/20 px-3 py-2.5 mb-3">
            <p className="text-[9px] font-semibold uppercase tracking-wider text-brand-700/80 dark:text-brand-300/80 mb-0.5">
              Você ganha por venda confirmada
            </p>
            <div className="flex items-baseline gap-1.5 flex-wrap">
              <span className="text-xl font-bold text-brand-700 dark:text-brand-300 tabular-nums leading-none">
                {formatBRL(earningsPerSale)}
              </span>
              <span className="text-xs font-bold text-brand-700/70 dark:text-brand-300/70 tabular-nums">
                ({product.defaultAffiliateCommissionPercent.toFixed(1)}%)
              </span>
            </div>
            <p className="text-[10px] text-gray-500 dark:text-gray-400 mt-1.5 tabular-nums">
              Cliente paga{" "}
              <span className="font-semibold text-gray-700 dark:text-gray-300">
                {formatBRL(product.price)}
              </span>
            </p>
          </div>

          {/* Descrição completa. */}
          {product.description && (
            <div className="mb-3">
              <p className="text-[9px] font-semibold uppercase tracking-wider text-gray-400 dark:text-gray-500 mb-1">
                Descrição
              </p>
              <p className="text-xs text-gray-600 dark:text-gray-400 leading-relaxed whitespace-pre-wrap">
                {product.description}
              </p>
            </div>
          )}

          {/* Bloco contextual: estado da afiliação existente ou explicação
              de "como funciona" para quem ainda não solicitou. */}
          {effectiveStatus ? (
            <ExistingAffiliationCallout
              status={effectiveStatus}
              onClose={onClose}
            />
          ) : (
            <div className="rounded-lg bg-gray-50 dark:bg-gray-800/60 ring-1 ring-gray-200/60 dark:ring-gray-700/60 px-3 py-2.5">
              <p className="text-[10px] font-semibold uppercase tracking-wider text-gray-500 dark:text-gray-400 mb-1">
                Como funciona
              </p>
              <p className="text-xs text-gray-600 dark:text-gray-400 leading-relaxed">
                {product.affiliationMode === "OPEN" ? (
                  <>
                    A afiliação é{" "}
                    <span className="font-semibold text-success-700 dark:text-success-300">
                      aprovada automaticamente
                    </span>{" "}
                    ao clicar em &ldquo;Afiliar&rdquo;. Você recebe seu link de tracking
                    imediatamente em &ldquo;Minhas afiliações&rdquo;.
                  </>
                ) : (
                  <>
                    A afiliação fica{" "}
                    <span className="font-semibold text-warning-700 dark:text-warning-300">
                      pendente de aprovação
                    </span>{" "}
                    do produtor. Você será notificado quando ele aprovar ou recusar
                    sua solicitação.
                  </>
                )}
              </p>
            </div>
          )}
        </div>

        {/* Footer sticky com CTA — sempre visível mesmo no scroll. CTA de
            afiliação some quando já existe um status registrado (o callout
            de existing affiliation no conteúdo já comunica o estado). */}
        <div className="shrink-0 border-t border-gray-200/80 dark:border-gray-800 bg-white dark:bg-gray-900 p-3">
          {error && (
            <div className="rounded-lg bg-error-50 dark:bg-error-500/10 ring-1 ring-error-200/60 dark:ring-error-500/20 px-3 py-2 text-[11px] text-error-700 dark:text-error-300 mb-2">
              {error}
            </div>
          )}

          <div className="flex items-center justify-end gap-2">
            <button
              type="button"
              onClick={onClose}
              className="h-9 inline-flex items-center justify-center rounded-lg px-3 text-xs font-semibold text-gray-700 dark:text-gray-300 hover:bg-gray-100 dark:hover:bg-gray-800 transition-colors"
            >
              {effectiveStatus ? "Fechar" : "Cancelar"}
            </button>
            {effectiveStatus === "APPROVED" && (
              <Link
                href="/affiliations"
                onClick={onClose}
                className="h-9 inline-flex items-center justify-center gap-1.5 rounded-lg bg-brand-500 hover:bg-brand-600 px-4 text-xs font-semibold text-white shadow-sm shadow-brand-500/20 transition-all hover:shadow-md hover:shadow-brand-500/25"
              >
                Ver meu link
                <svg
                  width="12"
                  height="12"
                  viewBox="0 0 24 24"
                  fill="none"
                  stroke="currentColor"
                  strokeWidth="2.5"
                  strokeLinecap="round"
                  strokeLinejoin="round"
                  aria-hidden="true"
                >
                  <line x1="5" y1="12" x2="19" y2="12" />
                  <polyline points="12 5 19 12 12 19" />
                </svg>
              </Link>
            )}
            {effectiveStatus === null && (
              <button
                type="button"
                onClick={() => {
                  setError(null);
                  request.mutate();
                }}
                disabled={request.isPending}
                className="h-9 inline-flex items-center justify-center gap-1.5 rounded-lg bg-brand-500 hover:bg-brand-600 px-4 text-xs font-semibold text-white shadow-sm shadow-brand-500/20 transition-all hover:shadow-md hover:shadow-brand-500/25 disabled:opacity-50 disabled:hover:shadow-sm"
              >
                {request.isPending ? (
                  "Enviando..."
                ) : (
                  <>
                    {product.affiliationMode === "OPEN"
                      ? "Afiliar agora"
                      : "Solicitar afiliação"}
                    <svg
                      width="12"
                      height="12"
                      viewBox="0 0 24 24"
                      fill="none"
                      stroke="currentColor"
                      strokeWidth="2.5"
                      strokeLinecap="round"
                      strokeLinejoin="round"
                      aria-hidden="true"
                    >
                      <line x1="5" y1="12" x2="19" y2="12" />
                      <polyline points="12 5 19 12 12 19" />
                    </svg>
                  </>
                )}
              </button>
            )}
          </div>
        </div>
      </div>
    </div>
  );
}
