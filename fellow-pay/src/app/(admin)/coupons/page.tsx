"use client";

import { useMemo, useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { marketplaceService } from "@/services/marketplace.service";
import { EmptyChecklistState } from "@/components/marketplace/EmptyChecklistState";
import { Select } from "@/components/ui/Select";
import { PageHeader } from "@/components/ui/PageHeader";

/**
 * Painel unificado de cupons do produtor. Lista globais (válidos para todos os
 * produtos do tenant) + específicos de cada produto. Form de criação tem
 * toggle "Aplicar a:" → global vs produto específico (dropdown de produtos
 * próprios). Mesma política de absorção: produtor absorve o desconto via
 * residual, afiliados/co-producers recebem comissão proporcional ao preço final.
 *
 * Por que separar dessa página da tab "Cupons" dentro do produto?
 *   - Tab do produto: contexto de UM produto, foco em cupons específicos.
 *   - Página /coupons: visão geral, criar cupom global de uma vez sem entrar
 *     em produto a produto. Ex: PROMO_BLACK_FRIDAY = 20% off em tudo.
 */

function formatBRL(v: number) {
  return v.toLocaleString("pt-BR", { style: "currency", currency: "BRL" });
}

export default function CouponsPage() {
  const queryClient = useQueryClient();

  // Lista unificada — backend resolve global + per-product via /coupons/mine.
  const { data: coupons = [], isLoading } = useQuery({
    queryKey: ["coupons", "mine"],
    queryFn: () => marketplaceService.listMyCoupons(),
  });

  // Lista de produtos do seller para dropdown do form (quando "produto específico").
  // staleTime longo pq raramente muda durante a sessão.
  const { data: productsData } = useQuery({
    queryKey: ["products", "list-all-for-coupons"],
    queryFn: () => marketplaceService.listMyProducts({ page: 1, pageSize: 100 }),
    staleTime: 5 * 60_000,
  });
  const products = productsData?.items ?? [];

  // === Form state ===
  const [code, setCode] = useState("");
  const [scope, setScope] = useState<"GLOBAL" | "PRODUCT">("GLOBAL");
  const [productId, setProductId] = useState<string>("");
  const [type, setType] = useState<0 | 1>(0); // 0=PERCENT, 1=FIXED
  const [value, setValue] = useState("");
  const [validUntil, setValidUntil] = useState("");
  const [maxUses, setMaxUses] = useState("");
  const [formError, setFormError] = useState<string | null>(null);

  // Preview do desconto. Pra cupom global mostra "X% off" / "-R$ Y" abstrato.
  // Pra cupom de produto, calcula sobre o price do produto selecionado.
  const previewLine = useMemo(() => {
    const n = parseFloat(value.replace(",", "."));
    if (!Number.isFinite(n) || n <= 0) return null;
    if (scope === "PRODUCT") {
      const p = products.find((x) => x.id === productId);
      if (!p) return null;
      const discount = type === 0 ? Math.min(p.price * n / 100, p.price) : Math.min(n, p.price);
      const final = p.price - discount;
      return `Cliente paga ${formatBRL(final)} em ${p.name}`;
    }
    return type === 0
      ? `-${n.toFixed(1)}% no preço de qualquer produto`
      : `-${formatBRL(n)} no preço de qualquer produto`;
  }, [scope, productId, products, type, value]);

  // === Filter state ===
  const [filter, setFilter] = useState<"ALL" | "GLOBAL" | "PRODUCT">("ALL");
  const visibleCoupons = useMemo(() => {
    if (filter === "GLOBAL") return coupons.filter((c) => c.productId === null);
    if (filter === "PRODUCT") return coupons.filter((c) => c.productId !== null);
    return coupons;
  }, [coupons, filter]);

  const create = useMutation({
    mutationFn: () => {
      const v = parseFloat(value.replace(",", "."));
      if (!code.trim()) throw new Error("Código é obrigatório.");
      if (!Number.isFinite(v) || v <= 0) throw new Error("Valor deve ser maior que zero.");
      if (type === 0 && v > 100) throw new Error("Percentual não pode passar de 100%.");
      if (scope === "PRODUCT" && !productId)
        throw new Error("Selecione o produto ao qual o cupom se aplica.");
      const mu = maxUses.trim() ? parseInt(maxUses.trim(), 10) : undefined;
      if (mu !== undefined && (!Number.isFinite(mu) || mu < 1)) {
        throw new Error("Limite de usos deve ser pelo menos 1.");
      }
      return marketplaceService.createCoupon({
        productId: scope === "PRODUCT" ? productId : undefined,
        code: code.trim().toUpperCase(),
        type,
        value: v,
        validUntil: validUntil ? new Date(validUntil).toISOString() : undefined,
        maxUses: mu,
      });
    },
    onSuccess: () => {
      setCode("");
      setValue("");
      setValidUntil("");
      setMaxUses("");
      setProductId("");
      setFormError(null);
      queryClient.invalidateQueries({ queryKey: ["coupons"] });
      // Invalida também a lista por-produto para a tab "Cupons" dentro do produto
      // refletir caso o usuário acabou de criar para um produto específico.
      queryClient.invalidateQueries({ queryKey: ["product-coupons"] });
    },
    onError: (err) => {
      setFormError(err instanceof Error ? err.message : "Erro ao criar cupom.");
    },
  });

  const remove = useMutation({
    mutationFn: (couponId: string) => marketplaceService.deleteCoupon(couponId),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ["coupons"] });
      queryClient.invalidateQueries({ queryKey: ["product-coupons"] });
    },
  });

  return (
    <div className="space-y-6 max-w-5xl">
      <PageHeader
        size="hero"
        title="Cupons de desconto"
        subtitle="Gerencie cupons globais (válidos para todos os seus produtos) ou amarrados a um produto específico. O desconto sai do residual do produtor — afiliados/co-producers recebem comissão proporcional ao preço final."
        decorIcon={
          <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.2" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true">
            <path d="M2 9a3 3 0 0 1 0 6v2a2 2 0 0 0 2 2h16a2 2 0 0 0 2-2v-2a3 3 0 0 1 0-6V7a2 2 0 0 0-2-2H4a2 2 0 0 0-2 2z" />
            <line x1="9" y1="14" x2="15" y2="8" />
            <circle cx="9.5" cy="9.5" r="0.5" fill="currentColor" />
            <circle cx="14.5" cy="13.5" r="0.5" fill="currentColor" />
          </svg>
        }
      />

      {/* === Form de criação colapsável ===
          Antes ocupava metade da tela permanente — agora vira `<details>`
          que fecha por default. Botão "+ Novo cupom" no header expande.
          Dá mais espaço para lista, que é o que o produtor mais consulta. */}
      <details className="group rounded-2xl border border-gray-200/80 bg-white dark:border-gray-800 dark:bg-gray-900 overflow-hidden">
        <summary className="flex items-center justify-between gap-3 px-5 py-4 cursor-pointer list-none hover:bg-gray-50/50 dark:hover:bg-white/[0.02] transition-colors">
          <div className="flex items-center gap-2">
            <span className="inline-flex items-center justify-center w-7 h-7 rounded-full bg-brand-500 text-white text-base font-bold leading-none">
              +
            </span>
            <div>
              <p className="text-sm font-medium text-gray-900 dark:text-white">Novo cupom</p>
              <p className="text-[11px] text-gray-500 dark:text-gray-400 mt-0.5">
                Global para todos os seus produtos ou amarrado a um específico
              </p>
            </div>
          </div>
          <svg
            width="16" height="16" viewBox="0 0 24 24" fill="none"
            stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round"
            aria-hidden="true"
            className="text-gray-400 dark:text-gray-500 shrink-0 transition-transform group-open:rotate-180"
          >
            <polyline points="6 9 12 15 18 9" />
          </svg>
        </summary>
        <div className="px-5 pb-5 pt-1 border-t border-gray-100 dark:border-gray-800/50">

        <div className="space-y-4 pt-4">
          {/* Toggle scope: global vs produto específico */}
          <Field label="Aplicar a">
            <div className="grid grid-cols-2 gap-1 p-1 bg-gray-100 dark:bg-gray-800 rounded-lg">
              <button
                type="button"
                onClick={() => { setScope("GLOBAL"); setProductId(""); setFormError(null); }}
                className={`h-9 rounded-md text-xs font-medium transition-colors ${
                  scope === "GLOBAL"
                    ? "bg-white dark:bg-gray-900 text-gray-900 dark:text-white shadow-sm"
                    : "text-gray-600 dark:text-gray-400 hover:text-gray-900 dark:hover:text-white"
                }`}
              >
                Todos os meus produtos
              </button>
              <button
                type="button"
                onClick={() => { setScope("PRODUCT"); setFormError(null); }}
                className={`h-9 rounded-md text-xs font-medium transition-colors ${
                  scope === "PRODUCT"
                    ? "bg-white dark:bg-gray-900 text-gray-900 dark:text-white shadow-sm"
                    : "text-gray-600 dark:text-gray-400 hover:text-gray-900 dark:hover:text-white"
                }`}
              >
                Produto específico
              </button>
            </div>
          </Field>

          {scope === "PRODUCT" && (
            <Field label="Produto">
              <Select
                ariaLabel="Produto do cupom"
                placeholder="Selecione..."
                value={productId}
                onChange={(v) => { setProductId(v); setFormError(null); }}
                options={products.map((p) => ({ value: p.id, label: p.name }))}
              />
            </Field>
          )}

          <div className="grid grid-cols-1 md:grid-cols-[1fr_140px_140px] gap-3">
            <Field label="Código">
              <input
                value={code}
                onChange={(e) => { setCode(e.target.value.toUpperCase()); setFormError(null); }}
                placeholder="BLACKFRIDAY"
                maxLength={32}
                className="h-10 w-full rounded-lg border border-gray-200 dark:border-gray-700 bg-white dark:bg-gray-900 px-3 text-sm font-mono uppercase tabular-nums"
              />
            </Field>
            <Field label="Tipo">
              <Select
                ariaLabel="Tipo de desconto"
                value={String(type)}
                onChange={(v) => setType(parseInt(v, 10) as 0 | 1)}
                options={[
                  { value: "0", label: "Percentual" },
                  { value: "1", label: "Valor fixo" },
                ]}
              />
            </Field>
            <Field label={type === 0 ? "Desconto (%)" : "Desconto (R$)"}>
              <input
                type="number"
                step={type === 0 ? "0.1" : "0.01"}
                min={type === 0 ? "0.1" : "0.01"}
                max={type === 0 ? "100" : undefined}
                value={value}
                onChange={(e) => { setValue(e.target.value); setFormError(null); }}
                placeholder={type === 0 ? "20" : "50.00"}
                className="h-10 w-full rounded-lg border border-gray-200 dark:border-gray-700 bg-white dark:bg-gray-900 px-3 text-sm tabular-nums"
              />
            </Field>
          </div>

          {previewLine && (
            <div className="rounded-lg bg-success-50 dark:bg-success-500/10 px-3 py-2 text-xs text-success-700 dark:text-success-400">
              Preview: {previewLine}
            </div>
          )}

          <div className="grid grid-cols-1 md:grid-cols-2 gap-3">
            <Field label="Válido até (opcional)">
              <input
                type="date"
                value={validUntil}
                onChange={(e) => setValidUntil(e.target.value)}
                className="h-10 w-full rounded-lg border border-gray-200 dark:border-gray-700 bg-white dark:bg-gray-900 px-3 text-sm tabular-nums"
              />
            </Field>
            <Field label="Limite de usos (opcional)">
              <input
                type="number"
                min="1"
                step="1"
                value={maxUses}
                onChange={(e) => setMaxUses(e.target.value)}
                placeholder="Sem limite"
                className="h-10 w-full rounded-lg border border-gray-200 dark:border-gray-700 bg-white dark:bg-gray-900 px-3 text-sm tabular-nums"
              />
            </Field>
          </div>

          {formError && (
            <div className="rounded-lg bg-error-50 dark:bg-error-500/10 px-3 py-2 text-xs text-error-700 dark:text-error-400">
              {formError}
            </div>
          )}

          <div className="flex justify-end">
            <button
              onClick={() => create.mutate()}
              disabled={create.isPending || !code.trim() || !value.trim() || (scope === "PRODUCT" && !productId)}
              className="h-10 rounded-lg bg-brand-500 hover:bg-brand-600 px-4 text-sm font-semibold text-white disabled:opacity-50"
            >
              {create.isPending ? "Criando..." : "Criar cupom"}
            </button>
          </div>
        </div>
        </div>
      </details>

      {/* === Filter + lista === */}
      <div className="flex items-center gap-1 border-b border-gray-200 dark:border-gray-800">
        {(["ALL", "GLOBAL", "PRODUCT"] as const).map((t) => (
          <button
            key={t}
            onClick={() => setFilter(t)}
            className={`h-10 px-4 text-sm font-medium border-b-2 -mb-px transition-colors ${
              filter === t
                ? "border-brand-500 text-brand-600 dark:text-brand-400"
                : "border-transparent text-gray-500 dark:text-gray-400 hover:text-gray-700 dark:hover:text-gray-300"
            }`}
          >
            {t === "ALL" ? "Todos" : t === "GLOBAL" ? "Globais" : "Por produto"}
          </button>
        ))}
        <span className="ml-auto text-xs text-gray-500 dark:text-gray-400 tabular-nums">
          {visibleCoupons.length} {visibleCoupons.length === 1 ? "cupom" : "cupons"}
        </span>
      </div>

      {isLoading ? (
        <p className="text-sm text-gray-500">Carregando...</p>
      ) : visibleCoupons.length === 0 ? (
        filter !== "ALL" ? (
          // Filter ativo sem resultados — feedback simples, sem checklist.
          <div className="rounded-2xl border border-gray-200/80 bg-white dark:border-gray-800 dark:bg-gray-900 p-10 text-center">
            <p className="text-sm font-medium text-gray-700 dark:text-gray-300">
              {filter === "GLOBAL" ? "Nenhum cupom global" : "Nenhum cupom por produto"}
            </p>
            <p className="text-xs text-gray-500 dark:text-gray-400 mt-1">
              Tente outra aba ou crie um cupom novo no botão acima.
            </p>
          </div>
        ) : (
          // Empty state inicial: checklist orientando o uso.
          <EmptyChecklistState
            title="Você ainda não criou cupons"
            subtitle="Cupons aumentam conversão em momentos sazonais (Black Friday, lançamento, recuperação)."
            steps={[
              {
                icon: "🎟",
                title: "Crie seu primeiro cupom",
                description: "Use o botão '+ Novo cupom' acima. Comece com 10-15% para testar.",
              },
              {
                icon: "🏷",
                title: "Escolha o escopo",
                description: "Global serve para todos os seus produtos; específico ajuda em lançamentos pontuais.",
              },
              {
                icon: "📢",
                title: "Divulgue o código",
                description: "Compartilhe nas suas redes, email marketing ou direto para os afiliados promoverem.",
              },
              {
                icon: "📊",
                title: "Acompanhe os usos",
                description: "O contador (usos / limite) aparece em cada cupom da lista após criados.",
              },
            ]}
          />
        )
      ) : (
        <ul className="rounded-2xl border border-gray-200 bg-white dark:border-gray-800 dark:bg-white/[0.03] divide-y divide-gray-100 dark:divide-gray-800">
          {visibleCoupons.map((c) => {
            const exhausted = c.maxUses !== null && c.usedCount >= c.maxUses;
            const expired = c.validUntil !== null && new Date(c.validUntil) < new Date();
            const inactive = exhausted || expired;
            return (
              <li key={c.id} className="flex items-center justify-between gap-3 px-5 py-4">
                <div className="min-w-0 flex-1">
                  <div className="flex items-center gap-2 flex-wrap mb-1">
                    <code className="text-sm font-mono font-semibold text-gray-900 dark:text-white">
                      {c.code}
                    </code>
                    <span className={`inline-flex items-center px-2 py-0.5 rounded-md text-[10px] uppercase tracking-wider font-bold ${
                      inactive
                        ? "bg-gray-100 text-gray-500 dark:bg-gray-800 dark:text-gray-500"
                        : "bg-success-50 text-success-700 dark:bg-success-500/15 dark:text-success-400"
                    }`}>
                      {exhausted ? "Esgotado" : expired ? "Expirado" : "Ativo"}
                    </span>
                    <span className="inline-flex items-center px-2 py-0.5 rounded-md text-[10px] uppercase tracking-wider font-medium bg-brand-50 text-brand-700 dark:bg-brand-500/15 dark:text-brand-400 tabular-nums">
                      {c.type === 0 ? `-${c.value.toFixed(1)}%` : `-${formatBRL(c.value)}`}
                    </span>
                    {c.productId === null ? (
                      <span className="inline-flex items-center px-2 py-0.5 rounded-md text-[10px] uppercase tracking-wider font-medium bg-gray-100 text-gray-600 dark:bg-gray-800 dark:text-gray-400">
                        Global
                      </span>
                    ) : (
                      <span className="inline-flex items-center px-2 py-0.5 rounded-md text-[10px] uppercase tracking-wider font-medium bg-gray-100 text-gray-600 dark:bg-gray-800 dark:text-gray-400 max-w-[180px] truncate">
                        {c.productName ?? "Produto"}
                      </span>
                    )}
                  </div>
                  <div className="flex items-baseline gap-4 text-[11px] text-gray-500 dark:text-gray-400 flex-wrap">
                    <span className="tabular-nums">
                      Usos:{" "}
                      <span className="font-medium text-gray-700 dark:text-gray-300">
                        {c.usedCount}{c.maxUses !== null ? ` / ${c.maxUses}` : ""}
                      </span>
                    </span>
                    {c.validUntil && (
                      <span>
                        Vence em{" "}
                        <span className="font-medium text-gray-700 dark:text-gray-300 tabular-nums">
                          {new Date(c.validUntil).toLocaleDateString("pt-BR")}
                        </span>
                      </span>
                    )}
                    <span className="text-gray-400 dark:text-gray-500 tabular-nums">
                      Criado em {new Date(c.createdAt).toLocaleDateString("pt-BR")}
                    </span>
                  </div>
                </div>
                <button
                  onClick={() => { if (confirm(`Remover cupom ${c.code}?`)) remove.mutate(c.id); }}
                  className="h-8 inline-flex items-center rounded-lg border border-error-200 dark:border-error-800 bg-white dark:bg-gray-900 px-3 text-xs font-medium text-error-700 dark:text-error-400 hover:bg-error-50 dark:hover:bg-error-500/10"
                >
                  Remover
                </button>
              </li>
            );
          })}
        </ul>
      )}
    </div>
  );
}

function Field({ label, children }: { label: string; children: React.ReactNode }) {
  return (
    <div>
      <label className="block text-[11px] font-medium text-gray-700 dark:text-gray-300 mb-1.5">
        {label}
      </label>
      {children}
    </div>
  );
}
