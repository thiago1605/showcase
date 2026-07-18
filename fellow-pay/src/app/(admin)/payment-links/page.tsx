"use client";
import React, { useState } from "react";
import { useScrollLock } from "@/hooks/useScrollLock";
import Link from "next/link";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { DataTable, Column } from "@/components/ui/DataTable";
import { paymentLinksService } from "@/services/payment-links.service";
import { splitRulesService } from "@/services/split-rules.service";
import { getCurrentSellerId } from "@/context/AuthContext";
import { PaymentMethodBadge } from "@/components/ui/PaymentMethodBadge";
import { Select } from "@/components/ui/Select";
import Input from "@/components/form/input/InputField";
import { PageHeader, PageHeaderButton } from "@/components/ui/PageHeader";
import type { PaymentLink } from "@/types";

function formatCurrency(value: number) {
  return new Intl.NumberFormat("pt-BR", { style: "currency", currency: "BRL" }).format(value);
}

function formatDate(dateStr: string) {
  return new Intl.DateTimeFormat("pt-BR", { day: "2-digit", month: "2-digit", year: "numeric" }).format(new Date(dateStr));
}

interface OwnedRuleOption {
  id: string;
  name: string;
}

type Method = "PIX" | "CREDIT_CARD" | "DEBIT_CARD" | "BOLETO";
const ALL_METHODS: Method[] = ["PIX", "CREDIT_CARD", "DEBIT_CARD", "BOLETO"];
const METHOD_LABELS: Record<Method, string> = {
  PIX: "Pix",
  CREDIT_CARD: "Cartão de Crédito",
  DEBIT_CARD: "Cartão de Débito",
  BOLETO: "Boleto",
};
// Mapa de cores por método foi extraído pra `<PaymentMethodBadge>` em
// `components/ui/PaymentMethodBadge.tsx` — única fonte de verdade.

const EMPTY_FORM = {
  description: "",
  amount: "",
  paymentTypes: ["PIX"] as Method[], // default minimal: 1 método selecionado
  installments: "1",
  maxUses: "", // vazio = ilimitado
  expiresAt: "",
  splitRuleId: "",
  // Modelo Híbrido: override per-link. "inherit" = sem override (TX herda flag do seller);
  // "force_on" / "force_off" = override explícito. UI mostra warning quando forced.
  advanceOptIn: "inherit" as "inherit" | "force_on" | "force_off",
};

interface EditFormState {
  id: string;
  description: string;
  paymentTypes: Method[];
  maxUses: string; // vazio = ilimitado
  expiresAt: string;
  splitRuleId: string;
  advanceOptIn: "inherit" | "force_on" | "force_off";
}

export default function PaymentLinksPage() {
  const queryClient = useQueryClient();
  const [page, setPage] = useState(1);
  const [showForm, setShowForm] = useState(false);
  const [formData, setFormData] = useState(EMPTY_FORM);
  const [formLoading, setFormLoading] = useState(false);
  const [formError, setFormError] = useState("");
  // Regras de split do próprio seller — usadas no select do formulário.
  // Carregado via React Query (não useEffect) para não cair em set-state-in-effect.
  const { data: ownedRules = [] } = useQuery({
    queryKey: ["payment-links", "owned-rules"],
    queryFn: async (): Promise<OwnedRuleOption[]> => {
      const mySellerId = getCurrentSellerId();
      if (!mySellerId) return [];
      try {
        const rules = await splitRulesService.list();
        return rules
          .filter((r) => r.isActive && r.ownerSellerId === mySellerId)
          .map((r) => ({ id: r.id, name: r.name }));
      } catch {
        return [];
      }
    },
    staleTime: 60 * 1000,
  });
  const [copiedId, setCopiedId] = useState<string | null>(null);
  const [toast, setToast] = useState<string | null>(null);
  const [editing, setEditing] = useState<EditFormState | null>(null);
  const [editLoading, setEditLoading] = useState(false);
  const [editError, setEditError] = useState("");
  // Filtro client-side por descrição — escala fácil até centenas de links.
  // Se a lista crescer pra milhares, mover pra query param + paginação server-side.
  const [search, setSearch] = useState("");

  // Trava scroll quando qualquer modal de criação/edição estiver aberto.
  useScrollLock(showForm || editing !== null);
  const pageSize = 20;

  const { data: allData = [], isLoading, error: queryError } = useQuery({
    queryKey: ["payment-links", "list"],
    queryFn: () => paymentLinksService.list(),
  });
  const loadError = queryError instanceof Error ? queryError.message : queryError ? "Não foi possível carregar os payment links." : null;
  const invalidate = () => queryClient.invalidateQueries({ queryKey: ["payment-links"] });


  const handleCopy = async (item: PaymentLink) => {
    try {
      await navigator.clipboard.writeText(item.url);
      setCopiedId(item.id);
      setToast("Link copiado para a área de transferência");
      setTimeout(() => setCopiedId(null), 2000);
      setTimeout(() => setToast(null), 2500);
    } catch {
      setToast("Não foi possível copiar. Selecione manualmente.");
      setTimeout(() => setToast(null), 3000);
    }
  };

  // Filtro case-insensitive na descrição. Aplicado antes da paginação pra que
  // os contadores e a navegação reflitam só o que matchou.
  const filtered = search.trim()
    ? allData.filter((l) =>
        (l.description ?? "").toLowerCase().includes(search.trim().toLowerCase()),
      )
    : allData;
  const totalCount = filtered.length;
  // Clamp da página durante render — quando o filtro encurta a lista e a
  // página atual fica fora do range, mostramos a primeira. Evita setState
  // dentro de useEffect (anti-pattern do react-hooks).
  const maxPage = Math.max(1, Math.ceil(totalCount / pageSize));
  const effectivePage = Math.min(page, maxPage);
  const start = (effectivePage - 1) * pageSize;
  const data = filtered.slice(start, start + pageSize);

  const handleCreate = async (e: React.FormEvent) => {
    e.preventDefault();
    setFormError("");
    if (formData.paymentTypes.length === 0) {
      setFormError("Selecione ao menos um método de pagamento.");
      return;
    }
    setFormLoading(true);
    try {
      const trimmedMaxUses = formData.maxUses.trim();
      // Quando o seller escolhe múltiplos métodos, mandamos paymentTypes; quando
      // só 1, fica equivalente ao modo legacy (paymentType single basta).
      const isMulti = formData.paymentTypes.length > 1;
      await paymentLinksService.create({
        amount: parseFloat(formData.amount),
        paymentType: formData.paymentTypes[0], // primário/default
        paymentTypes: isMulti ? formData.paymentTypes : undefined,
        installments: parseInt(formData.installments) || 1,
        description: formData.description,
        maxUses: trimmedMaxUses ? parseInt(trimmedMaxUses) : undefined,
        expiresAt: formData.expiresAt || undefined,
        splitRuleId: formData.splitRuleId || undefined,
        // Override só vai no payload quando o seller escolhe explicitamente.
        // "inherit" envia null/undefined pra TX herdar do flag global do seller.
        advanceOptIn:
          formData.advanceOptIn === "force_on" ? true :
          formData.advanceOptIn === "force_off" ? false : null,
      });
      setShowForm(false);
      setFormData(EMPTY_FORM);
      invalidate();
    } catch (err) {
      setFormError(err instanceof Error ? err.message : "Erro ao criar payment link.");
    }
    setFormLoading(false);
  };

  const toggleCreateMethod = (m: Method) => {
    setFormData((prev) => ({
      ...prev,
      paymentTypes: prev.paymentTypes.includes(m)
        ? prev.paymentTypes.filter((x) => x !== m)
        : [...prev.paymentTypes, m],
    }));
  };

  const setAllCreateMethods = (selectAll: boolean) => {
    setFormData((prev) => ({ ...prev, paymentTypes: selectAll ? [...ALL_METHODS] : [] }));
  };

  const handleStartEdit = (item: PaymentLink) => {
    setEditing({
      id: item.id,
      description: item.description ?? "",
      // paymentTypes vem do backend como string array, mas o type `PaymentLink.paymentTypes`
      // é PaymentType[] que aceita os mesmos identificadores. Cast seguro.
      paymentTypes: (item.paymentTypes as Method[]) ?? [item.paymentType as Method],
      maxUses: item.maxUses === null || item.maxUses === undefined ? "" : String(item.maxUses),
      expiresAt: item.expiresAt ? item.expiresAt.slice(0, 10) : "",
      splitRuleId: item.splitRuleId ?? "",
      advanceOptIn:
        item.advanceOptIn === true ? "force_on" :
        item.advanceOptIn === false ? "force_off" : "inherit",
    });
    setEditError("");
  };

  const toggleEditMethod = (m: Method) => {
    setEditing((prev) =>
      prev
        ? {
            ...prev,
            paymentTypes: prev.paymentTypes.includes(m)
              ? prev.paymentTypes.filter((x) => x !== m)
              : [...prev.paymentTypes, m],
          }
        : prev,
    );
  };
  const setAllEditMethods = (selectAll: boolean) => {
    setEditing((prev) => (prev ? { ...prev, paymentTypes: selectAll ? [...ALL_METHODS] : [] } : prev));
  };

  const handleSaveEdit = async (e: React.FormEvent) => {
    e.preventDefault();
    if (!editing) return;
    setEditError("");
    if (editing.paymentTypes.length === 0) {
      setEditError("Selecione ao menos um método de pagamento.");
      return;
    }
    setEditLoading(true);
    try {
      const trimmedMax = editing.maxUses.trim();
      await paymentLinksService.update(editing.id, {
        description: editing.description,
        maxUses: trimmedMax ? parseInt(trimmedMax) : null,
        expiresAt: editing.expiresAt || null,
        splitRuleId: editing.splitRuleId || null,
        paymentTypes: editing.paymentTypes,
        // Override de antecipação. "inherit" → null + reset; force_on/off → bool.
        advanceOptIn:
          editing.advanceOptIn === "force_on" ? true :
          editing.advanceOptIn === "force_off" ? false : null,
        advanceOptInReset: editing.advanceOptIn === "inherit",
      });
      setEditing(null);
      setToast("Link atualizado");
      setTimeout(() => setToast(null), 2500);
      invalidate();
    } catch (err) {
      setEditError(err instanceof Error ? err.message : "Erro ao salvar alterações.");
    }
    setEditLoading(false);
  };

  const columns: Column<PaymentLink>[] = [
    {
      key: "description",
      label: "Descrição",
      render: (item) => (
        // Descrição + chip "split" discreto + chip "inativo" só quando aplicável.
        // O chip "ativo" ficou redundante (todo link novo nasce ativo) — removemos
        // a coluna Status inteira e sinalizamos só estados que pedem ação.
        <div className="flex items-center gap-2 min-w-0">
          <span className="font-medium text-gray-900 dark:text-white truncate">
            {item.description || "—"}
          </span>
          {item.splitRuleId && (
            <span
              title="Este link aplica uma regra de split em cada pagamento"
              className="inline-flex flex-shrink-0 items-center rounded bg-gray-100 px-1.5 py-0.5 text-[9px] font-medium uppercase tracking-wider text-gray-600 dark:bg-gray-800 dark:text-gray-400"
            >
              split
            </span>
          )}
          {!item.active && (
            <span
              title="Link desativado — não aceita novas cobranças"
              className="inline-flex flex-shrink-0 items-center rounded bg-gray-100 px-1.5 py-0.5 text-[9px] font-medium uppercase tracking-wider text-gray-500 dark:bg-gray-800 dark:text-gray-400"
            >
              inativo
            </span>
          )}
        </div>
      ),
    },
    {
      key: "paymentType",
      label: "Métodos",
      render: (item) => {
        // paymentTypes sempre vem com ≥1; legacy = [paymentType].
        // Tamanho `xs` aperta os badges pra reduzir ruído visual em tabela densa.
        const types = (item.paymentTypes && item.paymentTypes.length > 0
          ? item.paymentTypes
          : [item.paymentType]) as string[];
        return (
          <div className="flex flex-wrap gap-1">
            {types.map((t) => (
              <PaymentMethodBadge key={String(t)} type={t} size="xs" />
            ))}
          </div>
        );
      },
    },
    {
      key: "amount",
      label: "Valor",
      render: (item) => (
        <span className="whitespace-nowrap text-gray-900 dark:text-white">{formatCurrency(item.amount)}</span>
      ),
    },
    {
      key: "usageCount",
      label: "Usos",
      render: (item) => {
        // Duas semânticas distintas precisam de tratamentos distintos:
        //
        // 1) maxUses = null (ilimitado): denominador "/ ∞" só polui o scan;
        //    mostramos o contador puro com sufixo singular/plural em PT.
        //
        // 2) maxUses definido: o que importa pro seller é saber se o link
        //    está chegando no fim. Usamos "X de Y" + cor que escala com
        //    uso (normal → warning ≥66% → error 100%) + barrinha de 2px
        //    de progresso. Quando exausto, chip "esgotado" substitui o texto.
        const used = item.usageCount;
        if (item.maxUses == null) {
          return (
            <span className="whitespace-nowrap tabular-nums text-gray-700 dark:text-gray-300">
              {used} {used === 1 ? "uso" : "usos"}
            </span>
          );
        }
        const max = item.maxUses;
        const ratio = max > 0 ? used / max : 0;
        const exhausted = used >= max;
        const nearLimit = !exhausted && ratio >= 2 / 3;
        // Cor escala com risco: cinza neutro → âmbar quando ≥66% → vermelho 100%.
        const textColor = exhausted
          ? "text-error-700 dark:text-error-400"
          : nearLimit
            ? "text-warning-700 dark:text-warning-400"
            : "text-gray-700 dark:text-gray-300";
        const barColor = exhausted
          ? "bg-error-500 dark:bg-error-400"
          : nearLimit
            ? "bg-warning-500 dark:bg-warning-400"
            : "bg-brand-500 dark:bg-brand-400";
        return (
          <div className="flex flex-col gap-1 min-w-[64px]">
            <div className="flex items-center gap-1.5 whitespace-nowrap">
              <span className={`tabular-nums text-sm ${textColor}`}>
                {used} <span className="text-gray-400 dark:text-gray-600">de</span> {max}
              </span>
              {exhausted && (
                <span
                  className="inline-flex items-center rounded bg-error-50 px-1.5 py-0.5 text-[9px] font-medium uppercase tracking-wider text-error-700 dark:bg-error-500/10 dark:text-error-400"
                  title="Link atingiu o limite máximo de usos"
                >
                  esgotado
                </span>
              )}
            </div>
            {/* Progress bar discreta — 2px. role=progressbar pra acessibilidade. */}
            <div
              role="progressbar"
              aria-valuenow={used}
              aria-valuemin={0}
              aria-valuemax={max}
              aria-label={`${used} de ${max} usos`}
              className="h-[2px] w-full overflow-hidden rounded-full bg-gray-100 dark:bg-gray-800"
            >
              <div
                className={`h-full ${barColor} transition-all`}
                style={{ width: `${Math.min(100, ratio * 100)}%` }}
              />
            </div>
          </div>
        );
      },
    },
    {
      key: "expiresAt",
      label: "Expira em",
      render: (item) =>
        item.expiresAt ? (
          <span className="whitespace-nowrap text-xs text-gray-600 dark:text-gray-400">
            {formatDate(item.expiresAt)}
          </span>
        ) : (
          // Em-dash discreto em vez de "Sem expiração" que quebrava em 2 linhas
          // numa coluna estreita. Padrão de table UX pra "nulo / não aplicável".
          <span aria-label="Sem expiração" className="text-gray-300 dark:text-gray-600">—</span>
        ),
    },
    {
      key: "actions",
      label: "",
      className: "w-48",
      render: (item) => {
        const isCopied = copiedId === item.id;
        return (
          // whitespace-nowrap nos botões pra evitar quebra em 2 linhas (bug do
          // estado anterior). justify-end pra ficar colado na direita.
          <div className="flex items-center gap-1.5 justify-end">
            <button
              type="button"
              onClick={() => handleStartEdit(item)}
              className="whitespace-nowrap rounded-lg px-2.5 py-1 text-xs font-medium text-gray-600 hover:bg-gray-100 hover:text-gray-900 dark:text-gray-400 dark:hover:bg-gray-800 dark:hover:text-gray-100"
              aria-label="Editar link"
            >
              Editar
            </button>
            <button
              type="button"
              onClick={() => handleCopy(item)}
              aria-label="Copiar link"
              className={`inline-flex items-center gap-1.5 whitespace-nowrap rounded-lg px-2.5 py-1 text-xs font-medium transition-colors ${
                isCopied
                  ? "bg-success-50 text-success-700 dark:bg-success-500/10 dark:text-success-400"
                  : "bg-brand-50 text-brand-700 hover:bg-brand-100 dark:bg-brand-500/10 dark:text-brand-400 dark:hover:bg-brand-500/15"
              }`}
            >
              {isCopied ? (
                <>
                  <svg width="14" height="14" viewBox="0 0 16 16" fill="none" aria-hidden="true">
                    <path d="M3.5 8.5L6.5 11.5L12.5 5" stroke="currentColor" strokeWidth="1.6" strokeLinecap="round" strokeLinejoin="round"/>
                  </svg>
                  Copiado
                </>
              ) : (
                <>
                  <svg width="14" height="14" viewBox="0 0 16 16" fill="none" aria-hidden="true">
                    <rect x="5" y="5" width="8" height="9" rx="1.5" stroke="currentColor" strokeWidth="1.4"/>
                    <path d="M3 11V3.5A1.5 1.5 0 0 1 4.5 2h6" stroke="currentColor" strokeWidth="1.4" strokeLinecap="round"/>
                  </svg>
                  Copiar link
                </>
              )}
            </button>
          </div>
        );
      },
    },
  ];

  return (
    <div className="space-y-6">
      <PageHeader
        size="hero"
        title="Links de pagamento"
        subtitle="Crie e gerencie links de pagamento."
        actions={<PageHeaderButton onClick={() => setShowForm(true)}>+ Novo link</PageHeaderButton>}
        decorIcon={
          <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.2" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true">
            <path d="M10 13a5 5 0 0 0 7.54.54l3-3a5 5 0 0 0-7.07-7.07l-1.72 1.71" />
            <path d="M14 11a5 5 0 0 0-7.54-.54l-3 3a5 5 0 0 0 7.07 7.07l1.71-1.71" />
          </svg>
        }
      />

      {loadError && (
        <div className="rounded-lg border border-error-200 bg-error-50 px-4 py-3 text-sm text-error-700 dark:border-error-500/30 dark:bg-error-500/10 dark:text-error-300">
          {loadError}
        </div>
      )}

      {/* Search bar — filtra client-side por descrição. Helper visual quando
          o seller tem dezenas de links de teste e produção misturados, sem
          forçar exclusão definitiva do dado. */}
      <div className="relative max-w-md">
        <svg
          width="16"
          height="16"
          viewBox="0 0 16 16"
          fill="none"
          aria-hidden="true"
          className="pointer-events-none absolute left-3 top-1/2 -translate-y-1/2 text-gray-400"
        >
          <circle cx="7" cy="7" r="5" stroke="currentColor" strokeWidth="1.5" />
          <path d="M11 11l3 3" stroke="currentColor" strokeWidth="1.5" strokeLinecap="round" />
        </svg>
        <input
          type="search"
          value={search}
          onChange={(e) => setSearch(e.target.value)}
          placeholder="Buscar por descrição…"
          aria-label="Filtrar links pela descrição"
          className="w-full rounded-lg border border-gray-200 bg-white py-2 pl-9 pr-3 text-sm text-gray-900 placeholder:text-gray-400 focus:border-brand-500 focus:outline-none focus:ring-1 focus:ring-brand-500 dark:border-gray-800 dark:bg-gray-900 dark:text-white"
        />
      </div>

      <DataTable<PaymentLink>
        columns={columns}
        data={data}
        page={effectivePage}
        pageSize={pageSize}
        totalCount={totalCount}
        onPageChange={setPage}
        isLoading={isLoading}
        emptyMessage={search.trim() ? "Nenhum link encontrado para essa busca." : "Nenhum payment link criado ainda."}
      />

      {/* Toast notification — discreto, canto inferior */}
      {toast && (
        <div
          role="status"
          aria-live="polite"
          className="fixed bottom-6 left-1/2 -translate-x-1/2 z-50 rounded-lg bg-gray-900 px-4 py-2.5 text-sm text-white shadow-lg dark:bg-gray-100 dark:text-gray-900"
        >
          {toast}
        </div>
      )}

      {/* Create form modal */}
      {showForm && (
        <div className="fixed inset-0 z-[100000] flex items-center justify-center bg-gray-400/5 backdrop-blur-[3px] modal-backdrop-in">
          <div className="w-full max-w-md rounded-xl bg-white p-6 dark:bg-gray-900 shadow-xl modal-content-in">
            <h3 className="text-lg font-semibold text-gray-900 dark:text-white mb-4">Novo link de pagamento</h3>
            {formError && (
              <div role="alert" className="mb-4 p-3 rounded-lg bg-error-50 dark:bg-error-500/10 text-error-700 dark:text-error-400 text-sm">{formError}</div>
            )}
            <form onSubmit={handleCreate} className="space-y-4">
              <Input
                label="Descrição"
                type="text"
                value={formData.description}
                onChange={(e) => setFormData({ ...formData, description: e.target.value })}
                required
              />
              <Input
                label="Valor (R$)"
                type="number"
                step={0.01}
                min="0.01"
                value={formData.amount}
                onChange={(e) => setFormData({ ...formData, amount: e.target.value })}
                required
              />
              <div>
                <div className="flex items-center justify-between mb-2">
                  <label className="block text-sm font-medium text-gray-700 dark:text-gray-300">Métodos aceitos</label>
                  <button
                    type="button"
                    onClick={() => setAllCreateMethods(formData.paymentTypes.length !== ALL_METHODS.length)}
                    className="text-xs font-medium text-brand-600 hover:text-brand-700 dark:text-brand-400"
                  >
                    {formData.paymentTypes.length === ALL_METHODS.length ? "Desmarcar todos" : "Selecionar todos"}
                  </button>
                </div>
                <div className="grid grid-cols-2 gap-2">
                  {ALL_METHODS.map((m) => {
                    const checked = formData.paymentTypes.includes(m);
                    return (
                      <label
                        key={m}
                        className={`flex items-center gap-2 rounded-lg border px-3 py-2 cursor-pointer transition-colors text-sm ${
                          checked
                            ? "border-brand-500 bg-brand-50 dark:bg-brand-500/10 text-brand-700 dark:text-brand-300"
                            : "border-gray-200 hover:border-gray-300 dark:border-gray-700 dark:hover:border-gray-600 text-gray-700 dark:text-gray-300"
                        }`}
                      >
                        <input
                          type="checkbox"
                          checked={checked}
                          onChange={() => toggleCreateMethod(m)}
                          className="h-4 w-4 accent-brand-500"
                        />
                        <span>{METHOD_LABELS[m]}</span>
                      </label>
                    );
                  })}
                </div>
                <p className="mt-2 text-xs text-gray-500 dark:text-gray-400">
                  O cliente verá esses métodos no checkout e escolhe um na hora de pagar.
                </p>
              </div>
              {/* P0-2 (2026-05-07): parcelamento real via Stripe ainda não confirmado
                  neste setup. Mantemos o campo travado em 1× para evitar prometer
                  parcelas que o adquirente não vai cobrar. */}
              <Input
                label="Parcelas"
                type="number"
                min="1"
                max="1"
                value="1"
                disabled
                hint="Cobrança à vista. Parcelamento ainda não disponível."
              />
              <div className="grid grid-cols-2 gap-3">
                <Input
                  label="Máx. usos"
                  type="number"
                  min="1"
                  value={formData.maxUses}
                  onChange={(e) => setFormData({ ...formData, maxUses: e.target.value })}
                  placeholder="Ilimitado"
                  hint="Deixe em branco para usos ilimitados."
                />
                <Input
                  label="Expira em"
                  type="date"
                  value={formData.expiresAt}
                  onChange={(e) => setFormData({ ...formData, expiresAt: e.target.value })}
                  hint="Opcional."
                />
              </div>
              <div>
                <label className="block text-sm font-medium text-gray-700 dark:text-gray-300 mb-1">Aplicar regra de split</label>
                <Select
                  value={formData.splitRuleId}
                  onChange={(v) => setFormData({ ...formData, splitRuleId: v })}
                  disabled={ownedRules.length === 0}
                  placeholder={ownedRules.length === 0 ? "Sem split (você ainda não tem regras ativas)" : "Sem split"}
                  options={[
                    { value: "", label: "Sem split" },
                    ...ownedRules.map((r) => ({ value: r.id, label: r.name })),
                  ]}
                />
                <p className="text-xs text-gray-500 dark:text-gray-400 mt-1">
                  Mostramos apenas regras criadas por você e ativas.{" "}
                  <Link href="/split-rules/new" className="text-brand-600 hover:text-brand-700 dark:text-brand-400">Criar nova regra</Link>.
                </p>
                <p className="text-xs text-gray-500 dark:text-gray-400 mt-1">
                  O split é aplicado em novas vendas pagas por este link. Vendas já existentes não são alteradas.
                </p>
              </div>

              {/* Modelo Híbrido: override per-link da antecipação automática.
                  Só aparece quando o link aceita CRÉDITO (único caso onde faz sentido). */}
              {formData.paymentTypes.includes("CREDIT_CARD") && (
                <div>
                  <label className="block text-sm font-medium text-gray-700 dark:text-gray-300 mb-1">Antecipação automática</label>
                  <Select
                    value={formData.advanceOptIn}
                    onChange={(v) => setFormData({ ...formData, advanceOptIn: v as "inherit" | "force_on" | "force_off" })}
                    options={[
                      { value: "inherit", label: "Seguir configuração da minha conta" },
                      { value: "force_on", label: "Forçar ADVANCE (recebo em D+30, cobra fee)" },
                      { value: "force_off", label: "Forçar parcelado (recebo mensalmente, sem fee de antecipação)" },
                    ]}
                  />
                  <p className="text-xs text-gray-500 dark:text-gray-400 mt-1">
                    Override aplicado a transações originadas deste link. Não afeta links anteriores.
                  </p>
                </div>
              )}

              <div className="flex justify-end gap-3 pt-2">
                <button type="button" onClick={() => setShowForm(false)} className="rounded-lg px-4 py-2 text-sm font-medium text-gray-600 hover:bg-gray-100 dark:text-gray-400 dark:hover:bg-gray-800">Cancelar</button>
                <button type="submit" disabled={formLoading} className="rounded-lg bg-brand-500 px-4 py-2 text-sm font-medium text-white hover:bg-brand-600 disabled:opacity-50">{formLoading ? "Criando..." : "Criar Link"}</button>
              </div>
            </form>
          </div>
        </div>
      )}

      {/* Edit modal — só campos não-financeiros (amount/paymentType são imutáveis) */}
      {editing && (
        <div className="fixed inset-0 z-[100000] flex items-center justify-center bg-gray-400/5 backdrop-blur-[3px] modal-backdrop-in">
          <div className="w-full max-w-md rounded-xl bg-white p-6 dark:bg-gray-900 shadow-xl modal-content-in">
            <h3 className="text-lg font-semibold text-gray-900 dark:text-white mb-1">Editar link de pagamento</h3>
            <p className="text-xs text-gray-500 dark:text-gray-400 mb-4">
              Valor não pode ser alterado após criação. Métodos aceitos podem ser ajustados —
              transações já processadas mantêm o método original gravado.
            </p>
            {editError && (
              <div role="alert" className="mb-4 p-3 rounded-lg bg-error-50 dark:bg-error-500/10 text-error-700 dark:text-error-400 text-sm">{editError}</div>
            )}
            <form onSubmit={handleSaveEdit} className="space-y-4">
              <div>
                <label className="block text-sm font-medium text-gray-700 dark:text-gray-300 mb-1">Descrição</label>
                <input
                  type="text"
                  value={editing.description}
                  onChange={(e) => setEditing({ ...editing, description: e.target.value })}
                  className="w-full rounded-lg border border-gray-200 px-4 py-2.5 text-sm dark:border-gray-700 dark:bg-gray-800 dark:text-white focus:border-brand-500 focus:outline-none"
                />
              </div>
              <div>
                <div className="flex items-center justify-between mb-2">
                  <label className="block text-sm font-medium text-gray-700 dark:text-gray-300">Métodos aceitos</label>
                  <button
                    type="button"
                    onClick={() => setAllEditMethods(editing.paymentTypes.length !== ALL_METHODS.length)}
                    className="text-xs font-medium text-brand-600 hover:text-brand-700 dark:text-brand-400"
                  >
                    {editing.paymentTypes.length === ALL_METHODS.length ? "Desmarcar todos" : "Selecionar todos"}
                  </button>
                </div>
                <div className="grid grid-cols-2 gap-2">
                  {ALL_METHODS.map((m) => {
                    const checked = editing.paymentTypes.includes(m);
                    return (
                      <label
                        key={m}
                        className={`flex items-center gap-2 rounded-lg border px-3 py-2 cursor-pointer transition-colors text-sm ${
                          checked
                            ? "border-brand-500 bg-brand-50 dark:bg-brand-500/10 text-brand-700 dark:text-brand-300"
                            : "border-gray-200 hover:border-gray-300 dark:border-gray-700 dark:hover:border-gray-600 text-gray-700 dark:text-gray-300"
                        }`}
                      >
                        <input
                          type="checkbox"
                          checked={checked}
                          onChange={() => toggleEditMethod(m)}
                          className="h-4 w-4 accent-brand-500"
                        />
                        <span>{METHOD_LABELS[m]}</span>
                      </label>
                    );
                  })}
                </div>
              </div>
              <div className="grid grid-cols-2 gap-3">
                <div>
                  <label className="block text-sm font-medium text-gray-700 dark:text-gray-300 mb-1">Máx. usos</label>
                  <input
                    type="number"
                    min="1"
                    value={editing.maxUses}
                    onChange={(e) => setEditing({ ...editing, maxUses: e.target.value })}
                    placeholder="Ilimitado"
                    className="w-full rounded-lg border border-gray-200 px-4 py-2.5 text-sm dark:border-gray-700 dark:bg-gray-800 dark:text-white focus:border-brand-500 focus:outline-none"
                  />
                  <p className="mt-1 text-xs text-gray-500 dark:text-gray-400">Vazio = ilimitado.</p>
                </div>
                <div>
                  <label className="block text-sm font-medium text-gray-700 dark:text-gray-300 mb-1">Expira em</label>
                  <input
                    type="date"
                    value={editing.expiresAt}
                    onChange={(e) => setEditing({ ...editing, expiresAt: e.target.value })}
                    className="w-full rounded-lg border border-gray-200 px-4 py-2.5 text-sm dark:border-gray-700 dark:bg-gray-800 dark:text-white focus:border-brand-500 focus:outline-none"
                  />
                  <p className="mt-1 text-xs text-gray-500 dark:text-gray-400">Vazio = sem expiração.</p>
                </div>
              </div>
              <div>
                <label className="block text-sm font-medium text-gray-700 dark:text-gray-300 mb-1">Aplicar regra de split</label>
                <Select
                  value={editing.splitRuleId}
                  onChange={(v) => setEditing({ ...editing, splitRuleId: v })}
                  disabled={ownedRules.length === 0}
                  placeholder={ownedRules.length === 0 ? "Sem split (você ainda não tem regras ativas)" : "Sem split"}
                  options={[
                    { value: "", label: "Sem split" },
                    ...ownedRules.map((r) => ({ value: r.id, label: r.name })),
                  ]}
                />
                <p className="text-xs text-gray-500 dark:text-gray-400 mt-1">
                  Mudança de regra só afeta novas vendas. Transações já processadas mantêm o snapshot original.
                </p>
              </div>

              {/* Override de antecipação — só pra links que aceitam CRÉDITO. */}
              {editing.paymentTypes.includes("CREDIT_CARD") && (
                <div>
                  <label className="block text-sm font-medium text-gray-700 dark:text-gray-300 mb-1">Antecipação automática</label>
                  <Select
                    value={editing.advanceOptIn}
                    onChange={(v) => setEditing({ ...editing, advanceOptIn: v as "inherit" | "force_on" | "force_off" })}
                    options={[
                      { value: "inherit", label: "Seguir configuração da minha conta" },
                      { value: "force_on", label: "Forçar ADVANCE (D+30, cobra fee)" },
                      { value: "force_off", label: "Forçar parcelado (sem fee de antecipação)" },
                    ]}
                  />
                  <p className="text-xs text-gray-500 dark:text-gray-400 mt-1">
                    Override aplicado a novas vendas. Transações já processadas mantêm o modo original.
                  </p>
                </div>
              )}

              <div className="flex justify-end gap-3 pt-2">
                <button type="button" onClick={() => setEditing(null)} className="rounded-lg px-4 py-2 text-sm font-medium text-gray-600 hover:bg-gray-100 dark:text-gray-400 dark:hover:bg-gray-800">Cancelar</button>
                <button type="submit" disabled={editLoading} className="rounded-lg bg-brand-500 px-4 py-2 text-sm font-medium text-white hover:bg-brand-600 disabled:opacity-50">{editLoading ? "Salvando..." : "Salvar"}</button>
              </div>
            </form>
          </div>
        </div>
      )}
    </div>
  );
}
