"use client";

import { useMemo, useState } from "react";
import Link from "next/link";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { marketplaceService } from "@/services/marketplace.service";
import { Select } from "@/components/ui/Select";
import { useScrollLock } from "@/hooks/useScrollLock";
import type { OrderBump } from "@/services/marketplace.service";

/**
 * Tab de Order Bumps — produtor configura ofertas adicionais que aparecem
 * dentro do checkout do produto principal. Cada bump aponta pra outro produto
 * existente do mesmo seller. Limite duro de 3 bumps ATIVOS (espelha
 * Kirvano/Hotmart — evita overwhelm visual no checkout).
 *
 * UI:
 *  - Lista atual com cover thumb, custom title, preço do produto referenciado,
 *    toggle ativo/inativo (mutate inline), botão "Editar" + botão "Remover".
 *  - Setas pra reordenar (up/down). Drag-and-drop ficou fora do escopo MVP —
 *    setas são consistentes em mobile.
 *  - Botão "Adicionar bump" abre modal com:
 *      - Select de produto (outros produtos do seller, exclui o atual)
 *      - Input title custom (placeholder com nome do produto selecionado)
 *      - Textarea description opcional
 *
 * O custom title é a chamada de marketing — ex: "Adicione o Bonus eBook por
 * apenas R$ 47!". O produtor controla 100% do texto exibido no checkout.
 *
 * Bumps cujo BumpProduct.status != PUBLISHED mostram badge de alerta. Ainda
 * são editáveis, mas o backend filtra fora no checkout público (não oferta).
 */
function formatBRL(v: number) {
  return v.toLocaleString("pt-BR", { style: "currency", currency: "BRL" });
}

const MAX_ACTIVE_BUMPS = 3;

export default function OrderBumpsTab({ productId }: { productId: string }) {
  const queryClient = useQueryClient();
  const [showCreate, setShowCreate] = useState(false);
  const [editing, setEditing] = useState<OrderBump | null>(null);

  const { data: bumps = [], isLoading } = useQuery({
    queryKey: ["product-order-bumps", productId],
    queryFn: () => marketplaceService.listOrderBumps(productId),
  });

  // Lista de outros produtos do seller (pra picker do modal). Excluímos o
  // próprio produto principal — bump não pode referenciar a si mesmo. Backend
  // valida; aqui é só pra UX limpa.
  const { data: myProducts } = useQuery({
    queryKey: ["my-products-for-bumps"],
    queryFn: () => marketplaceService.listMyProducts({ pageSize: 100 }),
    // Cache mais agressivo — lista de produtos do seller raramente muda
    // durante uma sessão de configuração de bump.
    staleTime: 60_000,
  });

  const availableBumpProducts = useMemo(() => {
    const items = myProducts?.items ?? [];
    // Filtra: != atual + (status == PUBLISHED OU PAUSED — não oferece DRAFT/ARCHIVED).
    // Ofertar DRAFT travaria o checkout no momento da compra; ARCHIVED não
    // faz sentido. PAUSED é OK porque produtor pode estar configurando bump
    // pra um produto que vai re-publicar em breve.
    return items.filter(
      (p) =>
        p.id !== productId &&
        (p.status === "PUBLISHED" || p.status === "PAUSED"),
    );
  }, [myProducts, productId]);

  const activeCount = bumps.filter((b) => b.isActive).length;
  const canAddMore = activeCount < MAX_ACTIVE_BUMPS;

  const remove = useMutation({
    mutationFn: (bumpId: string) => marketplaceService.deleteOrderBump(productId, bumpId),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ["product-order-bumps", productId] }),
  });

  const toggleActive = useMutation({
    mutationFn: ({ bumpId, isActive }: { bumpId: string; isActive: boolean }) =>
      marketplaceService.updateOrderBump(productId, bumpId, { isActive }),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ["product-order-bumps", productId] }),
  });

  // Move up/down: swap DisplayOrder com o vizinho da lista atual. Backend não
  // tem endpoint de "swap" — fazemos via 2 PUTs sequenciais. Race condition
  // teórica entre os 2 updates é benigna (lista volta no próximo refetch).
  const reorder = useMutation({
    mutationFn: async ({ index, direction }: { index: number; direction: "up" | "down" }) => {
      const target = direction === "up" ? index - 1 : index + 1;
      if (target < 0 || target >= bumps.length) return;
      const a = bumps[index];
      const b = bumps[target];
      await Promise.all([
        marketplaceService.updateOrderBump(productId, a.id, { displayOrder: b.displayOrder }),
        marketplaceService.updateOrderBump(productId, b.id, { displayOrder: a.displayOrder }),
      ]);
    },
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ["product-order-bumps", productId] }),
  });

  return (
    <div className="space-y-6 max-w-3xl">
      <div className="rounded-2xl border border-gray-200 bg-white dark:border-gray-800 dark:bg-white/[0.03] p-6">
        <div className="flex items-start justify-between gap-3 mb-4">
          <div>
            <p className="text-sm font-medium text-gray-900 dark:text-white">
              Order bumps
            </p>
            <p className="text-xs text-gray-500 dark:text-gray-400 mt-1">
              Ofertas adicionais exibidas dentro do checkout deste produto. Cada
              bump aponta para outro produto seu — o cliente marca o checkbox e
              o valor entra no total. Máximo de {MAX_ACTIVE_BUMPS} bumps ativos.
            </p>
          </div>
          <button
            type="button"
            onClick={() => setShowCreate(true)}
            disabled={!canAddMore}
            className="h-10 shrink-0 rounded-lg bg-brand-500 hover:bg-brand-600 px-4 text-sm font-semibold text-white disabled:opacity-50"
            title={!canAddMore ? `Máximo de ${MAX_ACTIVE_BUMPS} bumps ativos. Desative algum antes.` : undefined}
          >
            + Adicionar bump
          </button>
        </div>

        <div className="text-[11px] text-gray-500 dark:text-gray-400">
          {activeCount}/{MAX_ACTIVE_BUMPS} ativo{activeCount === 1 ? "" : "s"}
        </div>
      </div>

      {isLoading ? (
        <p className="text-sm text-gray-500">Carregando...</p>
      ) : bumps.length === 0 ? (
        <div className="rounded-2xl border border-dashed border-gray-300 dark:border-gray-700 p-8 text-center">
          <p className="text-sm text-gray-700 dark:text-gray-300">Nenhum order bump configurado</p>
          <p className="text-xs text-gray-500 dark:text-gray-400 mt-1">
            Adicione um bump para oferecer um produto extra no checkout. Order
            bumps são uma das maiores alavancas de ticket médio do Kirvano.
          </p>
        </div>
      ) : (
        <ul className="rounded-2xl border border-gray-200 bg-white dark:border-gray-800 dark:bg-white/[0.03] divide-y divide-gray-100 dark:divide-gray-800">
          {bumps.map((bump, index) => (
            <BumpRow
              key={bump.id}
              bump={bump}
              index={index}
              total={bumps.length}
              onEdit={() => setEditing(bump)}
              onRemove={() => {
                if (confirm(`Remover o bump "${bump.customTitle}"?`)) remove.mutate(bump.id);
              }}
              onToggle={() =>
                toggleActive.mutate({ bumpId: bump.id, isActive: !bump.isActive })
              }
              onMove={(direction) => reorder.mutate({ index, direction })}
            />
          ))}
        </ul>
      )}

      {showCreate && (
        <BumpFormModal
          mode="create"
          productId={productId}
          availableProducts={availableBumpProducts}
          onClose={() => setShowCreate(false)}
          onSaved={() => {
            setShowCreate(false);
            queryClient.invalidateQueries({ queryKey: ["product-order-bumps", productId] });
          }}
        />
      )}

      {editing && (
        <BumpFormModal
          mode="edit"
          productId={productId}
          availableProducts={availableBumpProducts}
          bump={editing}
          onClose={() => setEditing(null)}
          onSaved={() => {
            setEditing(null);
            queryClient.invalidateQueries({ queryKey: ["product-order-bumps", productId] });
          }}
        />
      )}
    </div>
  );
}

function BumpRow({
  bump,
  index,
  total,
  onEdit,
  onRemove,
  onToggle,
  onMove,
}: {
  bump: OrderBump;
  index: number;
  total: number;
  onEdit: () => void;
  onRemove: () => void;
  onToggle: () => void;
  onMove: (direction: "up" | "down") => void;
}) {
  // Status do produto referenciado: 0=DRAFT, 1=PUBLISHED, 2=PAUSED, 3=ARCHIVED.
  // Backend só oferta no checkout se PUBLISHED — qualquer outra coisa fica
  // visível pro produtor com badge de alerta pra ele resolver.
  const isPublished = bump.bumpProductStatus === 1;

  return (
    <li className="flex items-start gap-4 px-4 py-4">
      <div className="flex flex-col gap-1 shrink-0">
        <button
          type="button"
          onClick={() => onMove("up")}
          disabled={index === 0}
          aria-label="Mover para cima"
          className="h-6 w-6 inline-flex items-center justify-center rounded text-gray-400 hover:text-gray-700 dark:hover:text-gray-200 disabled:opacity-30"
        >
          <svg width="12" height="12" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
            <polyline points="18 15 12 9 6 15" />
          </svg>
        </button>
        <button
          type="button"
          onClick={() => onMove("down")}
          disabled={index === total - 1}
          aria-label="Mover para baixo"
          className="h-6 w-6 inline-flex items-center justify-center rounded text-gray-400 hover:text-gray-700 dark:hover:text-gray-200 disabled:opacity-30"
        >
          <svg width="12" height="12" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
            <polyline points="6 9 12 15 18 9" />
          </svg>
        </button>
      </div>

      <div className="h-14 w-20 shrink-0 rounded-md overflow-hidden bg-gray-100 dark:bg-gray-800">
        {bump.bumpProductCoverImageUrl ? (
          // eslint-disable-next-line @next/next/no-img-element
          <img
            src={bump.bumpProductCoverImageUrl}
            alt={bump.bumpProductName}
            className="h-full w-full object-cover"
          />
        ) : (
          <div className="h-full w-full bg-gradient-to-br from-brand-500/20 to-purple-500/20" />
        )}
      </div>

      <div className="flex-1 min-w-0">
        <div className="flex items-center gap-2 flex-wrap">
          <p className="text-sm font-medium text-gray-900 dark:text-white truncate">
            {bump.customTitle}
          </p>
          {!bump.isActive && (
            <span className="inline-flex items-center px-2 py-0.5 rounded-md text-[10px] uppercase tracking-wider font-bold bg-gray-100 text-gray-500 dark:bg-gray-800 dark:text-gray-500">
              Inativo
            </span>
          )}
          {!isPublished && (
            <span
              className="inline-flex items-center px-2 py-0.5 rounded-md text-[10px] uppercase tracking-wider font-bold bg-warning-50 text-warning-700 dark:bg-warning-500/15 dark:text-warning-400"
              title="O produto referenciado não está publicado — bump não aparece no checkout."
            >
              Produto não publicado
            </span>
          )}
        </div>
        <p className="text-[11px] text-gray-500 dark:text-gray-400 mt-1 truncate">
          Refere: <span className="font-medium text-gray-700 dark:text-gray-300">{bump.bumpProductName}</span>
          {" · "}
          {bump.discountAmount > 0 ? (
            <>
              <span className="line-through tabular-nums text-gray-400">
                {formatBRL(bump.bumpProductPrice)}
              </span>{" "}
              <span className="tabular-nums font-semibold text-success-700 dark:text-success-400">
                {formatBRL(Math.max(0, bump.bumpProductPrice - bump.discountAmount))}
              </span>{" "}
              <span className="inline-flex items-center px-1.5 py-0.5 rounded text-[9px] uppercase tracking-wider font-bold bg-success-50 text-success-700 dark:bg-success-500/15 dark:text-success-400">
                −{formatBRL(bump.discountAmount)}
              </span>
            </>
          ) : (
            <span className="tabular-nums">{formatBRL(bump.bumpProductPrice)}</span>
          )}
        </p>
        {bump.customDescription && (
          <p className="text-[11px] text-gray-500 dark:text-gray-400 mt-1 line-clamp-2">
            {bump.customDescription}
          </p>
        )}
      </div>

      <div className="flex items-center gap-2 shrink-0">
        <label className="inline-flex items-center gap-1.5 cursor-pointer select-none">
          <input
            type="checkbox"
            checked={bump.isActive}
            onChange={onToggle}
            className="h-4 w-4 rounded border-gray-300 text-brand-500 focus:ring-brand-500"
          />
          <span className="text-[11px] text-gray-600 dark:text-gray-400">Ativo</span>
        </label>
        <button
          type="button"
          onClick={onEdit}
          className="h-8 rounded-lg border border-gray-200 dark:border-gray-700 bg-white dark:bg-gray-900 px-3 text-xs font-medium text-gray-700 dark:text-gray-300 hover:bg-gray-50 dark:hover:bg-gray-800"
        >
          Editar
        </button>
        <button
          type="button"
          onClick={onRemove}
          className="h-8 rounded-lg border border-error-200 dark:border-error-800 bg-white dark:bg-gray-900 px-3 text-xs font-medium text-error-700 dark:text-error-400 hover:bg-error-50 dark:hover:bg-error-500/10"
        >
          Remover
        </button>
      </div>
    </li>
  );
}

/**
 * Modal de criação/edição. No create: select de produto + título + descrição.
 * No edit: só título + descrição (não permite mudar o BumpProductId — pra trocar
 * o produto referenciado o produtor deve deletar e recriar — evita comportamento
 * confuso de "editou mas é outro produto agora").
 */
function BumpFormModal({
  mode,
  productId,
  availableProducts,
  bump,
  onClose,
  onSaved,
}: {
  mode: "create" | "edit";
  productId: string;
  availableProducts: { id: string; name: string; price: number }[];
  bump?: OrderBump;
  onClose: () => void;
  onSaved: () => void;
}) {
  const [bumpProductId, setBumpProductId] = useState<string>(
    mode === "edit" && bump ? bump.bumpProductId : "",
  );
  const [customTitle, setCustomTitle] = useState<string>(
    mode === "edit" && bump ? bump.customTitle : "",
  );
  const [customDescription, setCustomDescription] = useState<string>(
    mode === "edit" && bump?.customDescription ? bump.customDescription : "",
  );
  // Desconto absoluto em R$. Armazenado como string para permitir input livre
  // (vazio, decimais parciais); parseado no save.
  const [discountInput, setDiscountInput] = useState<string>(
    mode === "edit" && bump && bump.discountAmount > 0
      ? bump.discountAmount.toFixed(2).replace(".", ",")
      : "",
  );
  const [error, setError] = useState<string | null>(null);

  // BumpFormModal só monta quando aberto (consumer faz `{open && <BumpFormModal />}`),
  // logo `true` aqui é suficiente — o cleanup do hook restaura overflow no unmount.
  useScrollLock(true);

  const selectedProduct = availableProducts.find((p) => p.id === bumpProductId);

  // Auto-preenche o título sugerido quando o produtor escolhe o produto (só no
  // create, e só se o título ainda não foi customizado). Reduz fricção: cliente
  // marketeiro raramente escreve o título do zero.
  const defaultTitleFor = (productName: string) =>
    `Adicione ${productName} ao seu pedido`;

  // Parse robusto: aceita "5", "5,00", "5.00", "R$ 5,00". NaN/negativo → 0.
  const parsedDiscount = (() => {
    const cleaned = discountInput
      .replace(/r\$|\s/gi, "")
      .replace(/\./g, "")
      .replace(",", ".");
    const n = parseFloat(cleaned);
    return !Number.isFinite(n) || n < 0 ? 0 : n;
  })();

  // Preço-referência do produto bump — usado para validar o desconto e mostrar
  // o "preço final" no hint. Em create, vem do select; em edit, do snapshot.
  const referencePrice =
    mode === "edit" && bump
      ? bump.bumpProductPrice
      : selectedProduct?.price ?? 0;
  const discountExceedsPrice =
    parsedDiscount > 0 && referencePrice > 0 && parsedDiscount > referencePrice;
  const finalBumpPrice = Math.max(0, referencePrice - parsedDiscount);

  const save = useMutation({
    mutationFn: async () => {
      if (discountExceedsPrice)
        throw new Error(
          "O desconto não pode ser maior que o preço do produto bump.",
        );
      if (mode === "create") {
        if (!bumpProductId) throw new Error("Escolha o produto a ser ofertado.");
        if (!customTitle.trim()) throw new Error("Título é obrigatório.");
        return await marketplaceService.createOrderBump(productId, {
          bumpProductId,
          customTitle: customTitle.trim(),
          customDescription: customDescription.trim() || undefined,
          discountAmount: parsedDiscount,
        });
      } else if (bump) {
        if (!customTitle.trim()) throw new Error("Título é obrigatório.");
        return await marketplaceService.updateOrderBump(productId, bump.id, {
          customTitle: customTitle.trim(),
          // Empty string explícito remove (backend trata "" como null).
          customDescription: customDescription.trim(),
          discountAmount: parsedDiscount,
        });
      }
    },
    onSuccess: onSaved,
    onError: (err) => {
      setError(err instanceof Error ? err.message : "Erro ao salvar bump.");
    },
  });

  return (
    <div className="fixed inset-0 z-[100000] flex items-center justify-center bg-gray-400/5 backdrop-blur-[3px] px-4 modal-backdrop-in">
      <div className="w-full max-w-lg rounded-2xl bg-white dark:bg-gray-900 shadow-2xl p-6 modal-content-in">
        <div className="flex items-start justify-between mb-4">
          <div>
            <h3 className="text-base font-semibold text-gray-900 dark:text-white">
              {mode === "create" ? "Adicionar order bump" : "Editar order bump"}
            </h3>
            <p className="text-xs text-gray-500 dark:text-gray-400 mt-1">
              {mode === "create"
                ? "Escolha um produto seu e a chamada de marketing para o checkout."
                : "Atualize a chamada de marketing exibida no checkout."}
            </p>
          </div>
          <button
            type="button"
            onClick={onClose}
            aria-label="Fechar"
            className="h-8 w-8 inline-flex items-center justify-center rounded-lg text-gray-400 hover:text-gray-700 dark:hover:text-gray-200"
          >
            <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
              <line x1="18" y1="6" x2="6" y2="18" />
              <line x1="6" y1="6" x2="18" y2="18" />
            </svg>
          </button>
        </div>

        <div className="space-y-4">
          {mode === "create" && (
            <div>
              <label className="block text-xs font-medium text-gray-700 dark:text-gray-300 mb-1.5">
                Produto a ser ofertado
              </label>
              {availableProducts.length === 0 ? (
                // Mensagem explícita sobre o requisito: bump precisa ser
                // OUTRO produto, não pode ser o atual. Usuário com 1 só
                // produto publicado (sendo este o próprio) pensa que o
                // sistema "não viu" o produto dele — explicitar.
                <div className="rounded-lg border border-warning-200 bg-warning-50 dark:border-warning-500/30 dark:bg-warning-500/10 p-3 space-y-2">
                  <p className="text-xs text-warning-800 dark:text-warning-300">
                    <strong>Bump precisa ser um produto diferente.</strong> Você
                    está configurando bumps deste produto — para oferecer um
                    add-on no checkout, precisa de ao menos mais 1 produto
                    publicado na sua conta.
                  </p>
                  <Link
                    href="/products/new"
                    className="inline-flex items-center h-8 rounded-md bg-warning-600 hover:bg-warning-700 px-3 text-xs font-semibold text-white transition-colors"
                  >
                    + Criar outro produto
                  </Link>
                </div>
              ) : (
                <Select
                  ariaLabel="Produto a ser ofertado"
                  value={bumpProductId}
                  onChange={(v) => {
                    setBumpProductId(v);
                    setError(null);
                    // Sugere título default só se ainda não foi customizado.
                    if (!customTitle.trim()) {
                      const p = availableProducts.find((x) => x.id === v);
                      if (p) setCustomTitle(defaultTitleFor(p.name));
                    }
                  }}
                  options={[
                    { value: "", label: "— Escolha um produto —" },
                    ...availableProducts.map((p) => ({
                      value: p.id,
                      label: `${p.name} — ${formatBRL(p.price)}`,
                    })),
                  ]}
                />
              )}
            </div>
          )}

          {mode === "edit" && bump && (
            <div className="rounded-lg bg-gray-50 dark:bg-gray-800 px-3 py-2 text-xs text-gray-700 dark:text-gray-300">
              Refere: <span className="font-medium">{bump.bumpProductName}</span> · <span className="tabular-nums">{formatBRL(bump.bumpProductPrice)}</span>
            </div>
          )}

          <div>
            <label className="block text-xs font-medium text-gray-700 dark:text-gray-300 mb-1.5">
              Título (chamada do checkout)
            </label>
            <input
              value={customTitle}
              onChange={(e) => { setCustomTitle(e.target.value); setError(null); }}
              placeholder={
                selectedProduct
                  ? defaultTitleFor(selectedProduct.name)
                  : "Adicione [produto] ao seu pedido"
              }
              maxLength={200}
              className="h-10 w-full rounded-lg border border-gray-200 dark:border-gray-700 bg-white dark:bg-gray-900 px-3 text-sm"
            />
            <p className="text-[10px] text-gray-500 dark:text-gray-400 mt-1">
              Aparece como headline do card do bump no checkout.
            </p>
          </div>

          <div>
            <label className="block text-xs font-medium text-gray-700 dark:text-gray-300 mb-1.5">
              Descrição (opcional)
            </label>
            <textarea
              value={customDescription}
              onChange={(e) => setCustomDescription(e.target.value)}
              placeholder="Sub-headline curta — ex: Aprenda também SQL avançado e domine o stack completo."
              maxLength={500}
              rows={3}
              className="w-full rounded-lg border border-gray-200 dark:border-gray-700 bg-white dark:bg-gray-900 px-3 py-2 text-sm"
            />
          </div>

          {/* Desconto exclusivo do bump — quando o cliente marca o checkbox no
              checkout, o valor cobrado pelo bump cai. Default 0 = sem desconto. */}
          <div>
            <label className="block text-xs font-medium text-gray-700 dark:text-gray-300 mb-1.5">
              Desconto exclusivo no checkout (opcional)
            </label>
            <div className="relative">
              <span className="pointer-events-none absolute left-3 top-1/2 -translate-y-1/2 text-xs font-semibold text-gray-400 dark:text-gray-500">
                R$
              </span>
              <input
                type="text"
                inputMode="decimal"
                value={discountInput}
                onChange={(e) => {
                  setDiscountInput(e.target.value);
                  setError(null);
                }}
                placeholder="0,00"
                className={`h-10 w-full rounded-lg border bg-white dark:bg-gray-900 pl-10 pr-3 text-sm tabular-nums ${
                  discountExceedsPrice
                    ? "border-error-400 dark:border-error-500"
                    : "border-gray-200 dark:border-gray-700"
                }`}
              />
            </div>
            {/* Hint dinâmico: mostra o preço final que o cliente paga ao marcar
                o bump, dando feedback imediato sobre o impacto do desconto. */}
            {parsedDiscount > 0 && referencePrice > 0 ? (
              discountExceedsPrice ? (
                <p className="text-[10px] text-error-600 dark:text-error-400 mt-1">
                  O desconto não pode ser maior que o preço do produto ({formatBRL(referencePrice)}).
                </p>
              ) : (
                <p className="text-[10px] text-gray-500 dark:text-gray-400 mt-1 tabular-nums">
                  Cliente paga{" "}
                  <span className="line-through text-gray-400">
                    {formatBRL(referencePrice)}
                  </span>{" "}
                  <span className="font-semibold text-success-700 dark:text-success-400">
                    {formatBRL(finalBumpPrice)}
                  </span>{" "}
                  pelo bump (economia de {formatBRL(parsedDiscount)}).
                </p>
              )
            ) : (
              <p className="text-[10px] text-gray-500 dark:text-gray-400 mt-1">
                Deixe em branco ou 0 para vender o bump pelo preço cheio. O desconto é aplicado apenas se o cliente marcar o checkbox do bump.
              </p>
            )}
          </div>

          {error && (
            <div className="rounded-lg bg-error-50 dark:bg-error-500/10 px-3 py-2 text-xs text-error-700 dark:text-error-400">
              {error}
            </div>
          )}
        </div>

        <div className="mt-6 flex items-center justify-end gap-2">
          <button
            type="button"
            onClick={onClose}
            className="h-10 rounded-lg border border-gray-200 dark:border-gray-700 bg-white dark:bg-gray-900 px-4 text-sm font-medium text-gray-700 dark:text-gray-300 hover:bg-gray-50 dark:hover:bg-gray-800"
          >
            Cancelar
          </button>
          <button
            type="button"
            onClick={() => save.mutate()}
            disabled={save.isPending || discountExceedsPrice || (mode === "create" && (!bumpProductId || availableProducts.length === 0))}
            className="h-10 rounded-lg bg-brand-500 hover:bg-brand-600 px-4 text-sm font-semibold text-white disabled:opacity-50"
          >
            {save.isPending
              ? "Salvando..."
              : mode === "create"
                ? "Adicionar bump"
                : "Salvar alterações"}
          </button>
        </div>
      </div>
    </div>
  );
}
