"use client";

import { use, useState } from "react";
import Link from "next/link";
import { useQuery, useMutation, useQueryClient } from "@tanstack/react-query";
import { marketplaceService } from "@/services/marketplace.service";
import { resolveCheckoutUrl } from "@/lib/url";
import { ImageUpload } from "@/components/ui/ImageUpload";
import { Select } from "@/components/ui/Select";
import { BackLink } from "@/components/ui/BackLink";
import { ConfirmModal } from "@/components/ui/ConfirmModal";
import OrderBumpsTab from "@/components/marketplace/OrderBumpsTab";
import type {
  Affiliation,
  AffiliationStatusCode,
  Product,
} from "@/types";

/**
 * Detalhe + gestão do produto pelo produtor. Duas abas:
 *  - "Visão geral" — dados editáveis (nome, preço, comissão, modo, etc.)
 *    + ações de lifecycle (publicar/pausar/arquivar).
 *  - "Afiliados" — lista de Affiliations do produto. PENDING para aprovar/rejeitar,
 *    APPROVED para revogar.
 */

const TAB = { OVERVIEW: "overview", AFFILIATES: "affiliates", ASSETS: "assets", BUMPS: "bumps", COUPONS: "coupons" } as const;
type TabId = (typeof TAB)[keyof typeof TAB];

const STATUS_LABEL: Record<string, string> = {
  DRAFT: "Rascunho",
  PUBLISHED: "Publicado",
  PAUSED: "Pausado",
  ARCHIVED: "Arquivado",
};

const AFFILIATION_STATUS_LABEL: Record<AffiliationStatusCode, string> = {
  PENDING: "Aguardando",
  APPROVED: "Aprovada",
  REJECTED: "Rejeitada",
  REVOKED: "Revogada",
};

const AFFILIATION_STATUS_CLS: Record<AffiliationStatusCode, string> = {
  PENDING: "bg-warning-50 text-warning-700 dark:bg-warning-500/15 dark:text-warning-400",
  APPROVED: "bg-success-50 text-success-700 dark:bg-success-500/15 dark:text-success-400",
  REJECTED: "bg-error-50 text-error-700 dark:bg-error-500/15 dark:text-error-400",
  REVOKED: "bg-gray-100 text-gray-500 dark:bg-gray-800 dark:text-gray-500",
};

function formatBRL(v: number) {
  return v.toLocaleString("pt-BR", { style: "currency", currency: "BRL" });
}

export default function ProductDetailPage({
  params,
}: {
  params: Promise<{ id: string }>;
}) {
  const { id } = use(params);
  const [tab, setTab] = useState<TabId>(TAB.OVERVIEW);
  const queryClient = useQueryClient();

  const { data: product, isLoading } = useQuery({
    queryKey: ["product", id],
    queryFn: () => marketplaceService.getProduct(id),
  });

  if (isLoading) return <div className="text-sm text-gray-500">Carregando...</div>;
  if (!product) return <div className="text-sm text-error-600">Produto não encontrado.</div>;

  function invalidate() {
    queryClient.invalidateQueries({ queryKey: ["product", id] });
    queryClient.invalidateQueries({ queryKey: ["products"] });
  }

  return (
    <div className="space-y-6">
      <BackLink fallbackHref="/products" label="Retornar" />

      {/* Hero card — cover à esquerda + info + ações à direita. Comunica
          imediatamente "qual produto você está editando" sem precisar ler
          texto. Inline metrics (preço, comissão, ganho por venda) dão
          contexto comercial sem o produtor precisar ir caçar em outra aba. */}
      <ProductHero product={product} onChange={invalidate} />

      {/* Checkout URL bar — só PUBLISHED/PAUSED (DRAFT/ARCHIVED não tem URL ativa) */}
      {(product.status === "PUBLISHED" || product.status === "PAUSED") && (
        <CheckoutUrlBar slug={product.slug} />
      )}

      <div className="flex items-center gap-1 border-b border-gray-200 dark:border-gray-800">
        <TabButton active={tab === TAB.OVERVIEW} onClick={() => setTab(TAB.OVERVIEW)}>
          Visão geral
        </TabButton>
        <TabButton active={tab === TAB.AFFILIATES} onClick={() => setTab(TAB.AFFILIATES)}>
          Afiliados
        </TabButton>
        <TabButton active={tab === TAB.ASSETS} onClick={() => setTab(TAB.ASSETS)}>
          Materiais
        </TabButton>
        <TabButton active={tab === TAB.BUMPS} onClick={() => setTab(TAB.BUMPS)}>
          Order Bumps
        </TabButton>
        <TabButton active={tab === TAB.COUPONS} onClick={() => setTab(TAB.COUPONS)}>
          Cupons
        </TabButton>
      </div>

      {tab === TAB.OVERVIEW && <OverviewTab product={product} onSaved={invalidate} />}
      {tab === TAB.AFFILIATES && <AffiliatesTab productId={product.id} />}
      {tab === TAB.ASSETS && <AssetsTab productId={product.id} />}
      {tab === TAB.BUMPS && <OrderBumpsTab productId={product.id} />}
      {tab === TAB.COUPONS && <CouponsTab productId={product.id} productPrice={product.price} />}
    </div>
  );
}

/**
 * Tab de cupons de desconto — produtor cria, lista e remove cupons amarrados
 * ao produto. Cada cupom tem code, tipo (PERCENT|FIXED), value, janela de
 * validade opcional e limite de usos opcional. Comprador aplica no checkout.
 *
 * Política do desconto: produtor absorve via residual (afiliados e
 * co-producers recebem comissão proporcional ao preço final). Está
 * documentado no Coupon entity.
 */
function CouponsTab({ productId, productPrice }: { productId: string; productPrice: number }) {
  const queryClient = useQueryClient();
  const { data: coupons = [], isLoading } = useQuery({
    queryKey: ["product-coupons", productId],
    queryFn: () => marketplaceService.listCoupons(productId),
  });

  // Form de criação. Code é uppercase enforced (o backend também normaliza).
  const [code, setCode] = useState("");
  const [type, setType] = useState<0 | 1>(0); // 0=PERCENT, 1=FIXED
  const [value, setValue] = useState("");
  const [validUntil, setValidUntil] = useState("");
  const [maxUses, setMaxUses] = useState("");
  const [formError, setFormError] = useState<string | null>(null);

  // Preview do desconto enquanto o produtor digita — feedback imediato pra
  // entender o impacto do cupom no preço final do produto.
  const previewDiscount = (() => {
    const n = parseFloat(value.replace(",", "."));
    if (!Number.isFinite(n) || n <= 0) return null;
    if (type === 0) {
      if (n > 100) return null;
      return Math.min(productPrice * n / 100, productPrice);
    }
    return Math.min(n, productPrice);
  })();

  const create = useMutation({
    mutationFn: () => {
      const v = parseFloat(value.replace(",", "."));
      if (!code.trim()) throw new Error("Código é obrigatório.");
      if (!Number.isFinite(v) || v <= 0) throw new Error("Valor deve ser maior que zero.");
      if (type === 0 && v > 100) throw new Error("Percentual não pode passar de 100%.");
      const mu = maxUses.trim() ? parseInt(maxUses.trim(), 10) : undefined;
      if (mu !== undefined && (!Number.isFinite(mu) || mu < 1)) {
        throw new Error("Limite de usos deve ser pelo menos 1.");
      }
      return marketplaceService.createCoupon({
        productId,
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
      setFormError(null);
      queryClient.invalidateQueries({ queryKey: ["product-coupons", productId] });
    },
    onError: (err) => {
      setFormError(err instanceof Error ? err.message : "Erro ao criar cupom.");
    },
  });

  const remove = useMutation({
    mutationFn: (couponId: string) => marketplaceService.deleteCoupon(couponId),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ["product-coupons", productId] }),
  });

  return (
    <div className="space-y-6 max-w-3xl">
      <div className="rounded-2xl border border-gray-200 bg-white dark:border-gray-800 dark:bg-white/[0.03] p-6">
        <p className="text-sm font-medium text-gray-900 dark:text-white mb-1">
          Criar cupom
        </p>
        <p className="text-xs text-gray-500 dark:text-gray-400 mb-4">
          Cupons amarrados a este produto. O desconto sai do residual do
          produtor — afiliados/co-producers recebem comissão proporcional ao
          preço final.
        </p>

        <div className="space-y-4">
          <div className="grid grid-cols-1 md:grid-cols-[1fr_140px_120px] gap-3">
            <Row label="Código">
              <input
                value={code}
                onChange={(e) => { setCode(e.target.value.toUpperCase()); setFormError(null); }}
                placeholder="PROMO10"
                maxLength={32}
                className="h-10 w-full rounded-lg border border-gray-200 dark:border-gray-700 bg-white dark:bg-gray-900 px-3 text-sm font-mono uppercase tabular-nums"
              />
            </Row>
            <Row label="Tipo">
              <Select
                ariaLabel="Tipo de desconto"
                value={String(type)}
                onChange={(v) => setType(parseInt(v, 10) as 0 | 1)}
                options={[
                  { value: "0", label: "Percentual" },
                  { value: "1", label: "Valor fixo" },
                ]}
              />
            </Row>
            <Row label={type === 0 ? "Desconto (%)" : "Desconto (R$)"}>
              <input
                type="number"
                step={type === 0 ? "0.1" : "0.01"}
                min={type === 0 ? "0.1" : "0.01"}
                max={type === 0 ? "100" : undefined}
                value={value}
                onChange={(e) => { setValue(e.target.value); setFormError(null); }}
                placeholder={type === 0 ? "10" : "20.00"}
                className="h-10 w-full rounded-lg border border-gray-200 dark:border-gray-700 bg-white dark:bg-gray-900 px-3 text-sm tabular-nums"
              />
            </Row>
          </div>

          {previewDiscount !== null && (
            <div className="rounded-lg bg-success-50 dark:bg-success-500/10 px-3 py-2 text-xs text-success-700 dark:text-success-400">
              Preview: cliente paga{" "}
              <strong className="tabular-nums">
                {formatBRL(productPrice - previewDiscount)}
              </strong>{" "}
              ({type === 0 ? `${parseFloat(value.replace(",", ".")).toFixed(1)}% off` : `-${formatBRL(previewDiscount)}`})
            </div>
          )}

          <div className="grid grid-cols-1 md:grid-cols-2 gap-3">
            <Row label="Válido até (opcional)">
              <input
                type="date"
                value={validUntil}
                onChange={(e) => setValidUntil(e.target.value)}
                className="h-10 w-full rounded-lg border border-gray-200 dark:border-gray-700 bg-white dark:bg-gray-900 px-3 text-sm tabular-nums"
              />
            </Row>
            <Row label="Limite de usos (opcional)">
              <input
                type="number"
                min="1"
                step="1"
                value={maxUses}
                onChange={(e) => setMaxUses(e.target.value)}
                placeholder="Sem limite"
                className="h-10 w-full rounded-lg border border-gray-200 dark:border-gray-700 bg-white dark:bg-gray-900 px-3 text-sm tabular-nums"
              />
            </Row>
          </div>

          {formError && (
            <div className="rounded-lg bg-error-50 dark:bg-error-500/10 px-3 py-2 text-xs text-error-700 dark:text-error-400">
              {formError}
            </div>
          )}

          <div className="flex justify-end">
            <button
              onClick={() => create.mutate()}
              disabled={create.isPending || !code.trim() || !value.trim()}
              className="h-10 rounded-lg bg-brand-500 hover:bg-brand-600 px-4 text-sm font-semibold text-white disabled:opacity-50"
            >
              {create.isPending ? "Criando..." : "Criar cupom"}
            </button>
          </div>
        </div>
      </div>

      <div>
        <p className="text-sm font-medium text-gray-900 dark:text-white mb-3">
          {coupons.length === 0
            ? "Nenhum cupom criado"
            : `${coupons.length} ${coupons.length === 1 ? "cupom" : "cupons"}`}
        </p>
        {isLoading ? (
          <p className="text-sm text-gray-500">Carregando...</p>
        ) : coupons.length > 0 ? (
          <ul className="rounded-2xl border border-gray-200 bg-white dark:border-gray-800 dark:bg-white/[0.03] divide-y divide-gray-100 dark:divide-gray-800">
            {coupons.map((c) => {
              const exhausted = c.maxUses !== null && c.usedCount >= c.maxUses;
              const expired = c.validUntil !== null && new Date(c.validUntil) < new Date();
              const inactive = exhausted || expired;
              return (
                <li key={c.id} className="flex items-center justify-between gap-3 px-4 py-3">
                  <div className="min-w-0 flex-1">
                    <div className="flex items-center gap-2 flex-wrap">
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
                    </div>
                    <div className="flex items-baseline gap-4 text-[11px] text-gray-500 dark:text-gray-400 mt-1 flex-wrap">
                      <span className="tabular-nums">
                        Usos: <span className="font-medium text-gray-700 dark:text-gray-300">
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
        ) : null}
      </div>
    </div>
  );
}

/**
 * Tab de materiais de divulgação — produtor sobe banners/copies/videos com URL
 * externa (Drive, S3, CDN) + tipo + título. Afiliados aprovados podem visualizar
 * essa lista para baixar e usar nas próprias campanhas.
 *
 * MVP: aceita URL externa para evitar limite de upload (videos pesados). Upload
 * direto via StorageController fica como follow-up.
 */
function AssetsTab({ productId }: { productId: string }) {
  const queryClient = useQueryClient();
  const { data: assets = [], isLoading } = useQuery({
    queryKey: ["product-assets", productId],
    queryFn: () => marketplaceService.listProductAssets(productId),
  });

  const [title, setTitle] = useState("");
  const [type, setType] = useState("banner");
  const [url, setUrl] = useState("");

  const add = useMutation({
    mutationFn: () =>
      marketplaceService.addProductAsset(productId, {
        title: title.trim(),
        type: type.trim() || "other",
        url: url.trim(),
      }),
    onSuccess: () => {
      setTitle("");
      setUrl("");
      queryClient.invalidateQueries({ queryKey: ["product-assets", productId] });
    },
  });

  const remove = useMutation({
    mutationFn: (assetId: string) => marketplaceService.deleteProductAsset(assetId),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ["product-assets", productId] }),
  });

  return (
    <div className="space-y-6 max-w-3xl">
      <div className="rounded-2xl border border-gray-200 bg-white dark:border-gray-800 dark:bg-white/[0.03] p-6">
        <p className="text-sm font-medium text-gray-900 dark:text-white mb-1">
          Adicionar material
        </p>
        <p className="text-xs text-gray-500 dark:text-gray-400 mb-4">
          Banners, vídeos, copies prontas para afiliados baixarem e usarem.
          Use URL do Drive, Dropbox, CDN ou qualquer storage público.
        </p>
        <div className="grid grid-cols-1 md:grid-cols-[1fr_140px] gap-3">
          <input
            value={title}
            onChange={(e) => setTitle(e.target.value)}
            placeholder="Título — ex: Banner Stories 1080×1920"
            maxLength={200}
            className="h-10 rounded-lg border border-gray-200 dark:border-gray-700 bg-white dark:bg-gray-900 px-3 text-sm"
          />
          <Select
            ariaLabel="Tipo de material"
            value={type}
            onChange={(v) => setType(v)}
            options={[
              { value: "banner", label: "Banner" },
              { value: "story", label: "Story / Reel" },
              { value: "video", label: "Vídeo" },
              { value: "copy", label: "Copy / Texto" },
              { value: "email", label: "Template e-mail" },
              { value: "other", label: "Outro" },
            ]}
          />
        </div>
        <input
          value={url}
          onChange={(e) => setUrl(e.target.value)}
          placeholder="URL — https://drive.google.com/..."
          maxLength={1000}
          className="mt-3 h-10 w-full rounded-lg border border-gray-200 dark:border-gray-700 bg-white dark:bg-gray-900 px-3 text-sm font-mono"
        />
        <div className="mt-4 flex justify-end">
          <button
            onClick={() => add.mutate()}
            disabled={add.isPending || !title.trim() || !url.trim()}
            className="h-10 rounded-lg bg-brand-500 hover:bg-brand-600 px-4 text-sm font-semibold text-white disabled:opacity-50"
          >
            {add.isPending ? "Salvando..." : "Adicionar material"}
          </button>
        </div>
      </div>

      <div>
        <p className="text-sm font-medium text-gray-900 dark:text-white mb-3">
          {assets.length === 0 ? "Nenhum material ainda" : `${assets.length} ${assets.length === 1 ? "material" : "materiais"}`}
        </p>
        {isLoading ? (
          <p className="text-sm text-gray-500">Carregando...</p>
        ) : assets.length > 0 ? (
          <ul className="rounded-2xl border border-gray-200 bg-white dark:border-gray-800 dark:bg-white/[0.03] divide-y divide-gray-100 dark:divide-gray-800">
            {assets.map((a) => (
              <li key={a.id} className="flex items-center justify-between gap-3 px-4 py-3">
                <div className="min-w-0 flex-1">
                  <p className="text-sm font-medium text-gray-900 dark:text-white truncate">{a.title}</p>
                  <p className="text-[11px] text-gray-500 dark:text-gray-400 truncate">
                    <span className="uppercase tracking-wider">{a.type}</span> · <a href={a.url} target="_blank" rel="noopener noreferrer" className="hover:text-brand-600 dark:hover:text-brand-400">{a.url}</a>
                  </p>
                </div>
                <div className="flex items-center gap-2 shrink-0">
                  <a href={a.url} target="_blank" rel="noopener noreferrer" className="h-8 inline-flex items-center rounded-lg border border-gray-200 dark:border-gray-700 bg-white dark:bg-gray-900 px-3 text-xs font-medium text-gray-700 dark:text-gray-300 hover:bg-gray-50 dark:hover:bg-gray-800">
                    Abrir
                  </a>
                  <button
                    onClick={() => { if (confirm("Remover este material?")) remove.mutate(a.id); }}
                    className="h-8 inline-flex items-center rounded-lg border border-error-200 dark:border-error-800 bg-white dark:bg-gray-900 px-3 text-xs font-medium text-error-700 dark:text-error-400 hover:bg-error-50 dark:hover:bg-error-500/10"
                  >
                    Remover
                  </button>
                </div>
              </li>
            ))}
          </ul>
        ) : null}
      </div>
    </div>
  );
}

function TabButton({
  active,
  onClick,
  children,
}: {
  active: boolean;
  onClick: () => void;
  children: React.ReactNode;
}) {
  return (
    <button
      onClick={onClick}
      className={`h-10 px-4 text-sm font-medium border-b-2 -mb-px transition-colors ${
        active
          ? "border-brand-500 text-brand-600 dark:text-brand-400"
          : "border-transparent text-gray-500 dark:text-gray-400 hover:text-gray-700 dark:hover:text-gray-300"
      }`}
    >
      {children}
    </button>
  );
}

/**
 * Barra com a URL pública do checkout do produto. Exibe:
 *  - O link completo (clicável + truncável)
 *  - "Copiar" para clipboard
 *  - "Abrir" em nova aba
 *
 * URL = `{origin}/p/{slug}`. Origin é resolvido client-side via `window.location.origin`
 * — funciona em qualquer ambiente (localhost, dev, prod) sem config extra.
 * SSR-safe via guard `typeof window`.
 */
function CheckoutUrlBar({ slug }: { slug: string }) {
  const [copied, setCopied] = useState(false);
  const origin = typeof window !== "undefined" ? window.location.origin : "";
  const url = `${origin}/p/${slug}`;

  async function copy() {
    try {
      await navigator.clipboard.writeText(url);
      setCopied(true);
      setTimeout(() => setCopied(false), 1500);
    } catch {
      /* clipboard pode estar bloqueado em contexto não-HTTPS — ignora */
    }
  }

  return (
    <div className="rounded-2xl border border-gray-200 dark:border-gray-800 bg-white dark:bg-white/[0.03] p-4 flex flex-wrap items-center gap-3">
      <div className="flex items-center gap-2 text-gray-500 dark:text-gray-400 shrink-0">
        <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true">
          <path d="M10 13a5 5 0 0 0 7.54.54l3-3a5 5 0 0 0-7.07-7.07l-1.72 1.71" />
          <path d="M14 11a5 5 0 0 0-7.54-.54l-3 3a5 5 0 0 0 7.07 7.07l1.71-1.71" />
        </svg>
        <span className="text-xs font-medium uppercase tracking-wider">Checkout público</span>
      </div>
      <code className="flex-1 min-w-0 text-xs font-mono text-gray-700 dark:text-gray-300 truncate" title={url}>
        {url}
      </code>
      <div className="flex items-center gap-2 shrink-0">
        <button
          onClick={copy}
          className="h-8 rounded-lg border border-gray-200 dark:border-gray-700 bg-white dark:bg-gray-900 px-3 text-xs font-medium text-gray-700 dark:text-gray-300 hover:bg-gray-50 dark:hover:bg-gray-800"
        >
          {copied ? "Copiado ✓" : "Copiar link"}
        </button>
        <a
          href={url}
          target="_blank"
          rel="noopener noreferrer"
          className="h-8 inline-flex items-center gap-1.5 rounded-lg bg-brand-500 hover:bg-brand-600 px-3 text-xs font-medium text-white"
        >
          <svg width="12" height="12" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true">
            <path d="M18 13v6a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V8a2 2 0 0 1 2-2h6" />
            <polyline points="15 3 21 3 21 9" />
            <line x1="10" y1="14" x2="21" y2="3" />
          </svg>
          Abrir
        </a>
      </div>
    </div>
  );
}

/**
 * Hero card no topo do detalhe — cover (40%) + info comercial (60%). Substitui
 * o header text-only que dava zero contexto visual. Padrão "dashboard de venda"
 * (Shopify / Hotmart / Kirvano admin) com:
 *  - cover proeminente para reconhecimento visual rápido
 *  - status badge prominente (não escondido no subtitle)
 *  - métricas comerciais inline (preço / comissão / ganho por venda)
 *  - ações de lifecycle agrupadas com hierarquia correta
 */
function ProductHero({ product, onChange }: { product: Product; onChange: () => void }) {
  // Mesmo cálculo + mesmo padrão visual do hero de /affiliations/[id]:
  // - Cover sm:w-72 + aspect-[16/9] + sm:m-4 + sm:rounded-xl
  // - flex-col sm:flex-row sm:items-center
  // - H1 text-xl, label row em cima, stickers de métricas abaixo
  // - Sticker do "Você recebe" destacado em brand-500 (produtor é sempre o
  //   viewer aqui, então essa é sempre a fatia dele)
  const earningsAffiliate = (product.price * product.defaultAffiliateCommissionPercent) / 100;
  const earningsProducer = product.price - earningsAffiliate;
  return (
    <div className="rounded-2xl border border-gray-200/80 bg-white dark:border-gray-800 dark:bg-gray-900 overflow-hidden">
      <div className="flex flex-col sm:flex-row sm:items-center">
        <div className="sm:w-72 shrink-0 aspect-[16/9] bg-gradient-to-br from-gray-100 to-gray-200 dark:from-gray-800 dark:to-gray-900 overflow-hidden sm:m-4 sm:rounded-xl">
          {product.coverImageUrl ? (
            // eslint-disable-next-line @next/next/no-img-element
            <img
              src={product.coverImageUrl}
              alt={product.name}
              className="w-full h-full object-cover"
            />
          ) : (
            <div className="w-full h-full flex items-center justify-center text-gray-400">
              <svg width="48" height="48" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true">
                <rect x="3" y="3" width="18" height="18" rx="2" />
                <circle cx="9" cy="9" r="2" />
                <path d="m21 15-3.086-3.086a2 2 0 0 0-2.828 0L6 21" />
              </svg>
            </div>
          )}
        </div>

        <div className="p-6 flex-1 min-w-0">
          <div className="flex items-start justify-between gap-3 flex-wrap">
            <div className="min-w-0 flex-1">
              {/* Label row — status pill + category pill + slug. Mesmo padrão
                  do hero de affiliations (label uppercase + pill ao lado). */}
              <div className="flex items-center gap-2 flex-wrap">
                <span className={`inline-flex items-center px-2 py-0.5 rounded-md text-[10px] uppercase tracking-wider font-semibold ${PRODUCT_STATUS_CLS[product.status] ?? ""}`}>
                  {STATUS_LABEL[product.status]}
                </span>
                {product.category && (
                  <span className="inline-flex items-center px-2 py-0.5 rounded-md text-[10px] uppercase tracking-wider font-semibold bg-gray-100 text-gray-600 dark:bg-gray-800 dark:text-gray-400">
                    {product.category}
                  </span>
                )}
                <span className="font-mono text-[11px] text-gray-500 dark:text-gray-500">
                  /{product.slug}
                </span>
              </div>
              <h1 className="mt-1 text-xl font-semibold text-gray-900 dark:text-white leading-tight">
                {product.name}
              </h1>
              {product.description && (
                <p className="text-xs text-gray-500 dark:text-gray-400 mt-0.5 line-clamp-2">
                  {product.description}
                </p>
              )}
            </div>
            <ProductActions product={product} onChange={onChange} />
          </div>

          {/* Stickers de métricas — mesmo padrão da page de affiliations:
              Preço (neutro) | Afiliado ganha · X% (neutro) | Você recebe · Y%
              (brand-500 solid). Produtor é sempre o viewer aqui → seu
              recebimento é o que tem highlight. */}
          {(() => {
            const neutralCard = "rounded-lg bg-gray-50 dark:bg-gray-800/60 px-3.5 py-2.5";
            const neutralLabel = "text-[10px] uppercase tracking-wider text-gray-500 dark:text-gray-400 mb-1 font-semibold";
            const neutralValue = "text-lg font-bold text-gray-900 dark:text-white tabular-nums leading-none";
            const highlightCard = "rounded-lg bg-brand-500 px-3.5 py-2.5 shadow-sm shadow-brand-500/20";
            const highlightLabel = "text-[10px] uppercase tracking-wider text-white/80 mb-1 font-semibold";
            const highlightValue = "text-lg font-bold text-white tabular-nums leading-none";
            const commissionPct = product.defaultAffiliateCommissionPercent;
            return (
              <div className="mt-3 flex flex-wrap gap-2">
                <div className={neutralCard}>
                  <p className={neutralLabel}>Preço</p>
                  <p className={neutralValue}>{formatBRL(product.price)}</p>
                </div>
                <div className={neutralCard}>
                  <p className={neutralLabel}>
                    Afiliado ganha · {commissionPct.toFixed(0)}%
                  </p>
                  <p className={neutralValue}>{formatBRL(earningsAffiliate)}</p>
                </div>
                <div className={highlightCard}>
                  <p className={highlightLabel}>
                    Você recebe · {(100 - commissionPct).toFixed(0)}%
                  </p>
                  <p className={highlightValue}>{formatBRL(earningsProducer)}</p>
                </div>
              </div>
            );
          })()}

          <p className="text-[10px] text-gray-500 dark:text-gray-400 mt-1.5">
            Valores brutos · taxas da plataforma são descontadas do produtor
          </p>
        </div>
      </div>
    </div>
  );
}

const PRODUCT_STATUS_CLS: Record<string, string> = {
  DRAFT: "bg-gray-100 text-gray-700 dark:bg-gray-800 dark:text-gray-300",
  PUBLISHED: "bg-success-50 text-success-700 dark:bg-success-500/15 dark:text-success-400",
  PAUSED: "bg-warning-50 text-warning-700 dark:bg-warning-500/15 dark:text-warning-400",
  ARCHIVED: "bg-gray-100 text-gray-500 dark:bg-gray-800 dark:text-gray-500",
};

function ProductActions({ product, onChange }: { product: Product; onChange: () => void }) {
  // Modal state separado por ação. Pausar e Arquivar precisam de
  // confirmação. Publicar e Retomar são imediatos (reversíveis com 1 clique).
  const [confirmingPause, setConfirmingPause] = useState(false);
  const [confirmingArchive, setConfirmingArchive] = useState(false);

  const publish = useMutation({ mutationFn: () => marketplaceService.publishProduct(product.id), onSuccess: onChange });
  const pause = useMutation({
    mutationFn: () => marketplaceService.pauseProduct(product.id),
    onSuccess: () => { onChange(); setConfirmingPause(false); },
  });
  const resume = useMutation({ mutationFn: () => marketplaceService.resumeProduct(product.id), onSuccess: onChange });
  const archive = useMutation({
    mutationFn: () => marketplaceService.archiveProduct(product.id),
    onSuccess: () => { onChange(); setConfirmingArchive(false); },
  });

  return (
    <div className="flex items-center gap-2 shrink-0">
      {product.status === "DRAFT" && (
        <button onClick={() => publish.mutate()} className="h-9 rounded-lg bg-brand-500 hover:bg-brand-600 px-3 text-sm font-medium text-white">
          Publicar
        </button>
      )}
      {product.status === "PUBLISHED" && (
        <button onClick={() => setConfirmingPause(true)} className="h-9 rounded-lg border border-gray-200 dark:border-gray-700 bg-white dark:bg-gray-900 px-3 text-sm font-medium text-gray-700 dark:text-gray-300 hover:bg-gray-50 dark:hover:bg-gray-800">
          Pausar
        </button>
      )}
      {product.status === "PAUSED" && (
        <button onClick={() => resume.mutate()} className="h-9 rounded-lg bg-brand-500 hover:bg-brand-600 px-3 text-sm font-medium text-white">
          Retomar
        </button>
      )}
      {product.status !== "ARCHIVED" && (
        <button
          onClick={() => setConfirmingArchive(true)}
          className="h-9 rounded-lg border border-error-200 dark:border-error-800 bg-white dark:bg-gray-900 px-3 text-sm font-medium text-error-700 dark:text-error-400 hover:bg-error-50 dark:hover:bg-error-500/10"
        >
          Arquivar
        </button>
      )}

      {/* Pausar — confirmação SEM requireCode. Ação reversível (1 clique no
          "Retomar" volta tudo), só precisa de proteção contra slip de mouse. */}
      <ConfirmModal
        isOpen={confirmingPause}
        title="Pausar este produto?"
        message="O checkout fica indisponível e novas vendas são bloqueadas. Afiliados continuam vendo o produto na lista, mas o link deixa de converter até você retomar. Reversível a qualquer momento."
        confirmLabel="Pausar"
        variant="default"
        isLoading={pause.isPending}
        onConfirm={() => pause.mutate()}
        onCancel={() => setConfirmingPause(false)}
      />

      {/* Arquivar — confirmação COM requireCode. Ação destrutiva
          irreversível (pelo menos na UI atual). Mesma proteção das demais
          ações importantes do app (team, webhooks, split-rules). */}
      <ConfirmModal
        isOpen={confirmingArchive}
        title="Arquivar este produto?"
        message="O produto some do marketplace, afiliações ativas perdem o link de conversão e novas vendas ficam permanentemente bloqueadas. Esta ação não pode ser revertida pela interface."
        confirmLabel="Arquivar"
        variant="danger"
        requireCode
        isLoading={archive.isPending}
        onConfirm={() => archive.mutate()}
        onCancel={() => setConfirmingArchive(false)}
      />
    </div>
  );
}

function OverviewTab({ product, onSaved }: { product: Product; onSaved: () => void }) {
  const [name, setName] = useState(product.name);
  const [description, setDescription] = useState(product.description ?? "");
  // Edição da capa:
  //   - existingCoverUrl: URL atual salva no produto (vem do server)
  //   - coverFile: novo arquivo escolhido nessa sessão (pendente upload)
  //   - coverCleared: flag indicando que user clicou "Remover" (precisa
  //     diferenciar "não mexi" de "quero deletar")
  const [existingCoverUrl, setExistingCoverUrl] = useState<string | null>(product.coverImageUrl ?? null);
  const [coverFile, setCoverFile] = useState<File | null>(null);
  const [coverCleared, setCoverCleared] = useState(false);
  const [price, setPrice] = useState(String(product.price));
  const [commission, setCommission] = useState(String(product.defaultAffiliateCommissionPercent));
  const [mode, setMode] = useState(product.affiliationMode);
  const [category, setCategory] = useState(product.category ?? "");
  const [deliveryUrl, setDeliveryUrl] = useState(product.deliveryUrl ?? "");
  // Tracking pixels — para afiliados que rodam ads pagos. Strings vazias = não
  // configurado / remover. Backend trata vazio como "limpar" (set null no DB).
  const [facebookPixelId, setFacebookPixelId] = useState(product.facebookPixelId ?? "");
  const [googleAdsConversionId, setGoogleAdsConversionId] = useState(product.googleAdsConversionId ?? "");

  const save = useMutation({
    mutationFn: async () => {
      // Two-phase submit (mesma lógica do /products/new):
      //   1. Se há arquivo pendente, faz upload e usa a URL nova
      //   2. Se user clicou "Remover" sem novo file, manda string vazia
      //      (backend interpreta como "limpar capa")
      //   3. Caso contrário, não toca em coverImageUrl
      let coverImageUrlToSave: string | undefined;
      if (coverFile) {
        coverImageUrlToSave = await marketplaceService.uploadProductCover(coverFile);
      } else if (coverCleared) {
        coverImageUrlToSave = ""; // backend Update aceita string vazia como "limpar"
      } else {
        coverImageUrlToSave = undefined; // não envia o campo no patch
      }
      return marketplaceService.updateProduct(product.id, {
        name,
        description,
        coverImageUrl: coverImageUrlToSave,
        price: parseFloat(price.replace(",", ".")),
        deliveryUrl: deliveryUrl || undefined,
        defaultAffiliateCommissionPercent: parseFloat(commission.replace(",", ".")),
        affiliationMode: mode,
        category: category || undefined,
        // Pixel IDs: trim → vazio significa "limpar" no backend (set null).
        // Manda undefined se o estado nunca foi tocado para evitar overrider o
        // campo no patch — mas hoje o state é inicializado com o valor existente,
        // então qualquer envio é intencional. OK passar sempre.
        facebookPixelId: facebookPixelId.trim(),
        googleAdsConversionId: googleAdsConversionId.trim(),
      });
    },
    onSuccess: (updated) => {
      // Reseta estado de edição de capa após salvar com sucesso
      setCoverFile(null);
      setCoverCleared(false);
      setExistingCoverUrl(updated.coverImageUrl ?? null);
      onSaved();
    },
  });

  // Dirty state detection — compara valores atuais vs os do product carregado.
  // Boolean para mostrar/esconder a sticky bottom bar; sem dirty, ela some.
  const isDirty =
    name !== product.name ||
    description !== (product.description ?? "") ||
    price !== String(product.price) ||
    commission !== String(product.defaultAffiliateCommissionPercent) ||
    mode !== product.affiliationMode ||
    category !== (product.category ?? "") ||
    deliveryUrl !== (product.deliveryUrl ?? "") ||
    facebookPixelId !== (product.facebookPixelId ?? "") ||
    googleAdsConversionId !== (product.googleAdsConversionId ?? "") ||
    coverFile !== null ||
    coverCleared;

  function discardChanges() {
    setName(product.name);
    setDescription(product.description ?? "");
    setPrice(String(product.price));
    setCommission(String(product.defaultAffiliateCommissionPercent));
    setMode(product.affiliationMode);
    setCategory(product.category ?? "");
    setDeliveryUrl(product.deliveryUrl ?? "");
    setFacebookPixelId(product.facebookPixelId ?? "");
    setGoogleAdsConversionId(product.googleAdsConversionId ?? "");
    setCoverFile(null);
    setCoverCleared(false);
  }

  // Live preview para aside — usa os valores ATUAIS do form, não os salvos.
  // Quando o produtor edita "Curso de SQL" → "Curso de SQL Master", o preview
  // atualiza imediatamente — sem precisar salvar primeiro.
  const previewCoverUrl = coverFile
    ? URL.createObjectURL(coverFile)
    : coverCleared ? null : existingCoverUrl;
  const previewCommission = parseFloat(commission.replace(",", "."));
  const previewPrice = parseFloat(price.replace(",", "."));

  return (
    // Layout 2 colunas em telas lg+: form (~66%) + aside sticky (~33%).
    // Em mobile vira 1 coluna stacked. Aside fica `sticky top-20` (compensa
    // altura do AppHeader sticky) para acompanhar scroll sem ficar oculto.
    // Pb-24 deixa espaço para bottom bar não cobrir o último FormSection.
    <div className="grid grid-cols-1 lg:grid-cols-[1fr_320px] gap-6 pb-24">
      <div className="space-y-4 min-w-0">
      <FormSection title="Identidade" subtitle="Como o produto aparece para o comprador" defaultOpen>
        <Row label="Nome">
          <input value={name} onChange={(e) => setName(e.target.value)} className="h-10 w-full rounded-lg border border-gray-200 dark:border-gray-700 bg-white dark:bg-gray-900 px-3 text-sm" />
        </Row>
        <Row label="Descrição">
          <textarea value={description} onChange={(e) => setDescription(e.target.value)} rows={4} className="w-full rounded-lg border border-gray-200 dark:border-gray-700 bg-white dark:bg-gray-900 px-3 py-2 text-sm" />
        </Row>
        <Row label="Capa do produto">
          {/* Cover já visível no hero acima e no preview à direita — aqui
              expõe o uploader para trocar. Hint reforça que é a mesma imagem
              renderizada em outros lugares da página. */}
          <ImageUpload
            existingUrl={coverCleared ? null : existingCoverUrl}
            pendingFile={coverFile}
            onPickFile={(file) => {
              setCoverFile(file);
              setCoverCleared(false);
            }}
            onRemove={() => {
              setCoverFile(null);
              setCoverCleared(true);
            }}
            emptyLabel="Clique para enviar a capa"
            hint="PNG/JPEG/WEBP até 5MB · 16:9 · esta imagem aparece no topo desta página, no preview à direita e no catálogo de afiliação"
          />
        </Row>
        <Row label="Categoria">
          <input value={category} onChange={(e) => setCategory(e.target.value)} placeholder="Ex: curso, ebook, mentoria" className="h-10 w-full rounded-lg border border-gray-200 dark:border-gray-700 bg-white dark:bg-gray-900 px-3 text-sm" />
        </Row>
      </FormSection>

      <FormSection title="Comercial" subtitle="Preço cobrado + entrega ao comprador" defaultOpen>
        <Row label="Preço (R$)">
          <input type="number" step="0.01" value={price} onChange={(e) => setPrice(e.target.value)} className="h-10 w-full rounded-lg border border-gray-200 dark:border-gray-700 bg-white dark:bg-gray-900 px-3 text-sm tabular-nums" />
        </Row>
        <Row label="URL de entrega">
          <input value={deliveryUrl} onChange={(e) => setDeliveryUrl(e.target.value)} placeholder="https://..." className="h-10 w-full rounded-lg border border-gray-200 dark:border-gray-700 bg-white dark:bg-gray-900 px-3 text-sm" />
          <p className="text-[11px] text-gray-500 dark:text-gray-400 mt-1">
            Link enviado por email após captura. Drive, área de membros, etc.
          </p>
        </Row>
      </FormSection>

      <FormSection title="Afiliação" subtitle="Comissão default e quem pode promover">
        <Row label="Comissão default (%)">
          <input type="number" step="0.1" min="0" max="100" value={commission} onChange={(e) => setCommission(e.target.value)} className="h-10 w-full rounded-lg border border-gray-200 dark:border-gray-700 bg-white dark:bg-gray-900 px-3 text-sm tabular-nums" />
          <p className="text-[11px] text-gray-500 dark:text-gray-400 mt-1">
            Aplicada a TODAS as afiliações. Override caso-a-caso ao aprovar.
          </p>
        </Row>
        <Row label="Modo de afiliação">
          <Select
            ariaLabel="Modo de afiliação"
            value={mode}
            onChange={(v) => setMode(v as typeof mode)}
            options={[
              { value: "OPEN", label: "Aberta — auto-aprova qualquer afiliado" },
              { value: "REQUEST", label: "Sob pedido — você aprova manualmente" },
              { value: "CLOSED", label: "Fechada — sem afiliações" },
            ]}
          />
        </Row>
      </FormSection>

      <FormSection
        title="Tracking de conversão"
        subtitle="Pixels para otimizar campanhas pagas dos afiliados"
      >
        <p className="text-xs text-gray-500 dark:text-gray-400">
          Configure pixels pros seus afiliados rastrearem conversões em campanhas pagas.
          Eventos disparados: <code className="bg-gray-100 dark:bg-gray-800 px-1 py-0.5 rounded text-[10px]">PageView</code> no carregamento,{" "}
          <code className="bg-gray-100 dark:bg-gray-800 px-1 py-0.5 rounded text-[10px]">Purchase</code> após captura.
        </p>
        <Row label="Facebook Pixel ID">
          <input
            value={facebookPixelId}
            onChange={(e) => setFacebookPixelId(e.target.value)}
            placeholder="123456789012345"
            maxLength={50}
            className="h-10 w-full rounded-lg border border-gray-200 dark:border-gray-700 bg-white dark:bg-gray-900 px-3 text-sm tabular-nums"
          />
        </Row>
        <Row label="Google Ads Conversion (AW-XXX/YYY)">
          <input
            value={googleAdsConversionId}
            onChange={(e) => setGoogleAdsConversionId(e.target.value)}
            placeholder="AW-123456789/abcDEF-ghi"
            maxLength={100}
            className="h-10 w-full rounded-lg border border-gray-200 dark:border-gray-700 bg-white dark:bg-gray-900 px-3 text-sm font-mono"
          />
        </Row>
      </FormSection>

      </div>

      {/* Aside direita — sticky em desktop. Mostra preview ao vivo (como o
          produto aparece no catálogo de afiliação) + dicas contextuais. O
          preview é poderoso: produtor edita preço/comissão/nome e vê
          imediatamente como vai aparecer pro afiliado. */}
      {/* `lg:top-20` (80px) compensa a altura do AppHeader sticky (~64-72px
          com py-4 + content). Sem isso, o topo do aside fica oculto pelo
          header conforme o usuário rola. */}
      <aside className="lg:sticky lg:top-20 lg:self-start space-y-4">
        <div className="rounded-2xl border border-gray-200/80 bg-white dark:border-gray-800 dark:bg-gray-900 overflow-hidden">
          <div className="px-4 py-3 border-b border-gray-100 dark:border-gray-800">
            <p className="text-[10px] font-semibold uppercase tracking-[0.06em] text-gray-500 dark:text-gray-400">
              Preview · como aparece para o afiliado
            </p>
          </div>
          <div className="aspect-[16/9] bg-gradient-to-br from-gray-100 to-gray-200 dark:from-gray-800 dark:to-gray-900 overflow-hidden flex items-center justify-center text-gray-400">
            {previewCoverUrl ? (
              // eslint-disable-next-line @next/next/no-img-element
              <img src={previewCoverUrl} alt="" className="w-full h-full object-cover" />
            ) : (
              <svg width="28" height="28" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.5" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true">
                <rect x="3" y="3" width="18" height="18" rx="2" />
                <circle cx="9" cy="9" r="2" />
                <path d="m21 15-3.086-3.086a2 2 0 0 0-2.828 0L6 21" />
              </svg>
            )}
          </div>
          <div className="p-4 space-y-2">
            <p className="text-sm font-semibold text-gray-900 dark:text-white line-clamp-2">
              {name || "Nome do produto"}
            </p>
            {description && (
              <p className="text-xs text-gray-500 dark:text-gray-400 line-clamp-2">
                {description}
              </p>
            )}
            <div className="grid grid-cols-2 gap-2 pt-2 border-t border-gray-100 dark:border-gray-800">
              <div>
                <p className="text-[10px] uppercase tracking-wider text-gray-500 dark:text-gray-400">Preço</p>
                <p className="text-sm font-semibold tabular-nums text-gray-900 dark:text-white">
                  {Number.isFinite(previewPrice) ? formatBRL(previewPrice) : "—"}
                </p>
              </div>
              <div>
                <p className="text-[10px] uppercase tracking-wider text-gray-500 dark:text-gray-400">Afiliado ganha</p>
                <p className="text-sm font-semibold tabular-nums text-brand-600 dark:text-brand-400">
                  {Number.isFinite(previewCommission) && Number.isFinite(previewPrice)
                    ? `${previewCommission.toFixed(1)}% · ${formatBRL(previewPrice * previewCommission / 100)}`
                    : "—"}
                </p>
              </div>
            </div>
          </div>
        </div>

        {/* Dicas — caixa terciária com guidelines contextuais. */}
        <div className="rounded-2xl border border-gray-200/80 bg-white dark:border-gray-800 dark:bg-gray-900 p-4 text-xs">
          <p className="text-[10px] font-semibold uppercase tracking-[0.06em] text-gray-500 dark:text-gray-400 mb-2">
            Dicas rápidas
          </p>
          <ul className="space-y-1.5 text-gray-500 dark:text-gray-400">
            <li>📸 Capa 16:9 + boa qualidade (1920×1080) vende mais</li>
            <li>💰 Comissão de 30%+ atrai afiliados sérios</li>
            <li>📝 Descrição direta e benefícios claros aumentam conversão</li>
            <li>🔗 URL de entrega: drive público, área de membros, etc.</li>
          </ul>
        </div>
      </aside>

      {/* Sticky bottom bar — só aparece quando há alterações. Padrão GitHub/
          Linear/Figma: feedback claro de "você tem mudanças não salvas" +
          ações de descartar/salvar lado a lado. Sem dirty state, some. */}
      {isDirty && (
        <div className="fixed bottom-0 left-0 right-0 lg:left-[290px] z-40 bg-white dark:bg-gray-900 border-t border-gray-200 dark:border-gray-800 shadow-[0_-4px_12px_-4px_rgba(0,0,0,0.1)] dark:shadow-[0_-4px_12px_-4px_rgba(0,0,0,0.4)]">
          <div className="max-w-6xl mx-auto px-6 py-3 flex items-center justify-between gap-3">
            <p className="text-sm text-gray-700 dark:text-gray-300">
              <span className="inline-flex items-center gap-1.5">
                <span className="w-2 h-2 rounded-full bg-warning-500" aria-hidden="true" />
                Alterações não salvas
              </span>
            </p>
            <div className="flex items-center gap-2">
              <button
                onClick={discardChanges}
                disabled={save.isPending}
                className="h-9 rounded-lg border border-gray-200 dark:border-gray-700 bg-white dark:bg-gray-900 px-3 text-sm font-medium text-gray-700 dark:text-gray-300 hover:bg-gray-50 dark:hover:bg-gray-800 disabled:opacity-50"
              >
                Descartar
              </button>
              <button
                onClick={() => save.mutate()}
                disabled={save.isPending}
                className="h-9 rounded-lg bg-brand-500 hover:bg-brand-600 px-4 text-sm font-semibold text-white disabled:opacity-50"
              >
                {save.isPending ? "Salvando..." : "Salvar alterações"}
              </button>
            </div>
          </div>
        </div>
      )}
    </div>
  );
}

/**
 * Seção colapsável com header clicável. Usa `<details>` nativo pro estado
 * + chevron animado. `defaultOpen` mantém abertas as 2 seções mais usadas
 * (Identidade + Comercial); Afiliação e Tracking começam fechadas — produtor
 * abre on-demand quando precisa configurar.
 */
function FormSection({
  title,
  subtitle,
  defaultOpen,
  children,
}: {
  title: string;
  subtitle?: string;
  defaultOpen?: boolean;
  children: React.ReactNode;
}) {
  return (
    <details
      open={defaultOpen}
      // SEM overflow-hidden — clipava dropdown do Select (Modo de afiliação)
      // que renderiza absolute relativo ao trigger. Rounded corners não bleed
      // sem overflow porque borders são inset.
      className="group rounded-2xl border border-gray-200/80 bg-white dark:border-gray-800 dark:bg-gray-900"
    >
      <summary className="flex items-center justify-between gap-3 px-5 py-4 cursor-pointer list-none hover:bg-gray-50/50 dark:hover:bg-white/[0.02] transition-colors">
        <div>
          <p className="text-sm font-semibold text-gray-900 dark:text-white">{title}</p>
          {subtitle && (
            <p className="text-[11px] text-gray-500 dark:text-gray-400 mt-0.5">{subtitle}</p>
          )}
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
      <div className="px-5 pb-5 pt-1 space-y-4 border-t border-gray-100 dark:border-gray-800/50">
        {children}
      </div>
    </details>
  );
}

function Row({ label, children }: { label: string; children: React.ReactNode }) {
  return (
    <div>
      <label className="block text-xs font-medium text-gray-700 dark:text-gray-300 mb-1.5">{label}</label>
      {children}
    </div>
  );
}

function AffiliatesTab({ productId }: { productId: string }) {
  const [filter, setFilter] = useState<AffiliationStatusCode | "">("");
  const [search, setSearch] = useState("");
  const queryClient = useQueryClient();

  const { data, isLoading } = useQuery({
    queryKey: ["product", productId, "affiliations", filter],
    queryFn: () =>
      marketplaceService.listProductAffiliations(productId, {
        status: filter || undefined,
        pageSize: 50,
      }),
  });

  const approve = useMutation({
    mutationFn: (id: string) => marketplaceService.approveAffiliation(id),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ["product", productId, "affiliations"] }),
  });
  const reject = useMutation({
    mutationFn: (id: string) => marketplaceService.rejectAffiliation(id),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ["product", productId, "affiliations"] });
      setRejectingId(null);
    },
  });
  const revoke = useMutation({
    mutationFn: (id: string) => marketplaceService.revokeAffiliation(id),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ["product", productId, "affiliations"] });
      setRevokingId(null);
    },
  });
  // State para controlar qual afiliação está em fluxo de confirmação. Modais
  // com requireCode (revoke) e sem (reject) para evitar click acidental em
  // ações destrutivas — alinha com o padrão do resto do app (team, webhooks,
  // split-rules, payment-links, reports, /affiliations/[id]).
  const [revokingId, setRevokingId] = useState<string | null>(null);
  const [rejectingId, setRejectingId] = useState<string | null>(null);

  const items = data?.items ?? [];

  // Leaderboard query — só roda quando tem APPROVED. Pode ser vazio (sem
  // vendas ainda), aí o componente não renderiza.
  const { data: leaderboard = [] } = useQuery({
    queryKey: ["product", productId, "leaderboard"],
    queryFn: () => marketplaceService.getProductLeaderboard(productId, 5),
    staleTime: 60_000,
  });

  // Filtragem client-side por nome/tracking. Paginação está em 50, então
  // filtrar local resolve a busca sem round-trip. Pra catálogos > 50 vai ter
  // que paginar/buscar server-side, mas no MVP isso é overkill.
  const filteredItems = items.filter((a) => {
    if (!search.trim()) return true;
    const q = search.trim().toLowerCase();
    return (
      (a.affiliateSellerName ?? "").toLowerCase().includes(q) ||
      a.trackingCode.toLowerCase().includes(q)
    );
  });

  // Labels e opções centralizadas para os chips de filtro de status. Ordem
  // intencional: Todas → Aguardando (urgente, action required) → ativas →
  // estados terminais.
  const STATUS_FILTERS: { value: AffiliationStatusCode | ""; label: string }[] = [
    { value: "", label: "Todas" },
    { value: "PENDING", label: "Aguardando" },
    { value: "APPROVED", label: "Aprovadas" },
    { value: "REJECTED", label: "Rejeitadas" },
    { value: "REVOKED", label: "Revogadas" },
  ];

  return (
    <div className="space-y-4">
      {/* Top performers — só renderiza se tem pelo menos 1 venda atribuída.
          Posicionado ANTES da listagem geral pq é a leitura mais útil pro
          produtor (quem está performando). Bg sutilmente diferente da
          listagem pra hierarquia visual. */}
      {leaderboard.length > 0 && (
        <div className="rounded-2xl border border-gray-200 bg-gradient-to-br from-warning-50/40 to-white dark:from-warning-500/5 dark:to-transparent dark:border-gray-800 dark:bg-white/[0.03] p-5">
          <div className="flex items-center justify-between mb-3">
            <p className="text-[10px] font-semibold uppercase tracking-[0.06em] text-gray-500 dark:text-gray-400">
              Top performers · {leaderboard.length === 1 ? "1 afiliado" : `${leaderboard.length} afiliados`}
            </p>
            <p className="text-[10px] text-gray-400 dark:text-gray-500">
              Por faturamento gerado
            </p>
          </div>
          <ol className="space-y-1">
            {leaderboard.map((e) => (
              <li key={e.affiliationId}>
                <Link
                  href={`/affiliations/${e.affiliationId}`}
                  className="group flex items-center gap-3 text-sm rounded-lg px-2 py-2 -mx-2 hover:bg-white/60 dark:hover:bg-white/[0.04] transition-colors"
                >
                  <span className={`inline-flex items-center justify-center w-6 h-6 rounded-full text-[11px] font-bold tabular-nums ${
                    e.rank === 1 ? "bg-warning-100 text-warning-700 dark:bg-warning-500/20 dark:text-warning-400" :
                    e.rank === 2 ? "bg-gray-200 text-gray-700 dark:bg-gray-700 dark:text-gray-300" :
                    e.rank === 3 ? "bg-amber-100 text-amber-700 dark:bg-amber-500/20 dark:text-amber-400" :
                    "bg-gray-100 text-gray-600 dark:bg-gray-800 dark:text-gray-400"
                  }`}>{e.rank}</span>
                  <span className="font-medium text-gray-900 dark:text-white group-hover:text-brand-600 dark:group-hover:text-brand-400 flex-1 truncate transition-colors">
                    {e.affiliateName ?? "—"}
                  </span>
                  <span className="text-xs text-gray-500 dark:text-gray-400 tabular-nums">
                    {e.salesCount} {e.salesCount === 1 ? "venda" : "vendas"}
                  </span>
                  <span className="font-semibold text-success-600 dark:text-success-400 tabular-nums w-24 text-right">
                    {e.tpv.toLocaleString("pt-BR", { style: "currency", currency: "BRL" })}
                  </span>
                  <svg
                    width="14" height="14" viewBox="0 0 24 24" fill="none"
                    stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round"
                    aria-hidden="true"
                    className="text-gray-300 dark:text-gray-600 group-hover:text-gray-500 dark:group-hover:text-gray-400 transition-colors shrink-0"
                  >
                    <polyline points="9 18 15 12 9 6" />
                  </svg>
                </Link>
              </li>
            ))}
          </ol>
        </div>
      )}

      {/* Toolbar: heading + search + filter chips. Layout sticky-feeling no
          topo da listagem. Search input à direita pra alinhar com o pattern
          de admin SaaS (busca no canto), chips de status numa segunda linha. */}
      <div className="space-y-3">
        <div className="flex items-end justify-between gap-3 flex-wrap">
          <div>
            <h2 className="text-base font-semibold text-gray-900 dark:text-white">
              Afiliados do produto
            </h2>
            <p className="text-xs text-gray-500 dark:text-gray-400 mt-0.5 tabular-nums">
              {data?.totalCount ?? 0} {(data?.totalCount ?? 0) === 1 ? "afiliação" : "afiliações"} no total
              {search.trim() && ` · ${filteredItems.length} ${filteredItems.length === 1 ? "resultado" : "resultados"}`}
            </p>
          </div>
          <div className="relative w-full sm:w-72">
            <input
              type="search"
              value={search}
              onChange={(e) => setSearch(e.target.value)}
              placeholder="Buscar por nome ou tracking…"
              className="h-9 w-full rounded-lg border border-gray-200 dark:border-gray-700 bg-white dark:bg-gray-900 pl-9 pr-3 text-sm text-gray-900 dark:text-white placeholder:text-gray-400 focus:outline-none focus:ring-1 focus:ring-brand-500"
            />
            <svg
              width="16" height="16" viewBox="0 0 24 24" fill="none"
              stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round"
              className="absolute left-3 top-1/2 -translate-y-1/2 text-gray-400"
              aria-hidden="true"
            >
              <circle cx="11" cy="11" r="8" />
              <line x1="21" y1="21" x2="16.65" y2="16.65" />
            </svg>
          </div>
        </div>

        <div className="inline-flex flex-wrap gap-0.5 p-0.5 bg-gray-100 dark:bg-gray-800 rounded-lg">
          {STATUS_FILTERS.map((s) => (
            <button
              key={s.value}
              type="button"
              onClick={() => setFilter(s.value)}
              aria-pressed={filter === s.value}
              className={`h-7 px-3 text-xs font-semibold rounded-md transition-colors ${
                filter === s.value
                  ? "bg-white dark:bg-gray-900 text-gray-900 dark:text-white shadow-sm"
                  : "text-gray-500 dark:text-gray-400 hover:text-gray-900 dark:hover:text-white"
              }`}
            >
              {s.label}
            </button>
          ))}
        </div>
      </div>

      {isLoading ? (
        // Skeletons preservam o layout — evita shift quando a data carrega.
        <ul className="rounded-2xl border border-gray-200 bg-white dark:border-gray-800 dark:bg-white/[0.03] divide-y divide-gray-100 dark:divide-gray-800 overflow-hidden">
          {[0, 1, 2].map((i) => (
            <li key={i} className="flex items-center gap-3 px-5 py-3.5">
              <div className="flex-1 space-y-2">
                <div className="h-4 w-40 bg-gray-100 dark:bg-gray-800 rounded animate-pulse" />
                <div className="h-3 w-60 bg-gray-100 dark:bg-gray-800 rounded animate-pulse" />
              </div>
              <div className="h-8 w-24 bg-gray-100 dark:bg-gray-800 rounded animate-pulse" />
            </li>
          ))}
        </ul>
      ) : filteredItems.length === 0 ? (
        <div className="rounded-2xl border border-gray-200 bg-white dark:border-gray-800 dark:bg-white/[0.03] p-10 text-center">
          <div className="inline-flex items-center justify-center w-12 h-12 rounded-full bg-gray-100 dark:bg-gray-800 mb-3">
            <svg width="22" height="22" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.8" strokeLinecap="round" strokeLinejoin="round" className="text-gray-400" aria-hidden="true">
              <path d="M17 21v-2a4 4 0 0 0-4-4H5a4 4 0 0 0-4 4v2" />
              <circle cx="9" cy="7" r="4" />
              <path d="M23 21v-2a4 4 0 0 0-3-3.87" />
              <path d="M16 3.13a4 4 0 0 1 0 7.75" />
            </svg>
          </div>
          <p className="text-sm font-medium text-gray-900 dark:text-white">
            {search.trim()
              ? "Nenhum afiliado encontrado"
              : filter
                ? "Sem afiliações neste status"
                : "Ainda sem afiliados"}
          </p>
          <p className="text-xs text-gray-500 dark:text-gray-400 mt-1 max-w-md mx-auto">
            {search.trim()
              ? `Nenhum afiliado bate com "${search.trim()}". Tente outro termo ou limpe a busca.`
              : filter === "PENDING"
                ? "Quando algum seller solicitar afiliação, aparece aqui para você aprovar ou rejeitar."
                : filter
                  ? "Mude o filtro acima para ver afiliações em outros estados."
                  : "Compartilhe o link do produto na aba Visão Geral. Sellers interessados solicitam afiliação."}
          </p>
        </div>
      ) : (
        <ul className="rounded-2xl border border-gray-200 bg-white dark:border-gray-800 dark:bg-white/[0.03] divide-y divide-gray-100 dark:divide-gray-800 overflow-hidden">
          {filteredItems.map((a) => (
            <AffiliationRow
              key={a.id}
              a={a}
              onApprove={() => approve.mutate(a.id)}
              onReject={() => setRejectingId(a.id)}
              onRevoke={() => setRevokingId(a.id)}
            />
          ))}
        </ul>
      )}

      {/* Modal de revoke — requireCode pq é ação destrutiva irreversível
          (afiliado para de converter, comissões futuras zeradas). Mesmo
          pattern de outras ações importantes do app. */}
      <ConfirmModal
        isOpen={!!revokingId}
        title="Revogar esta afiliação?"
        message="O link do afiliado deixa de converter imediatamente. Essa ação é irreversível — para reativar, o afiliado teria que solicitar uma nova afiliação."
        confirmLabel="Revogar"
        variant="danger"
        requireCode
        isLoading={revoke.isPending}
        onConfirm={() => revokingId && revoke.mutate(revokingId)}
        onCancel={() => setRevokingId(null)}
      />

      {/* Modal de reject — sem requireCode (action menos destrutiva: só
          declina uma solicitação PENDING, afiliado pode pedir de novo).
          Mas confirmação ainda é útil para evitar slip do mouse. */}
      <ConfirmModal
        isOpen={!!rejectingId}
        title="Rejeitar esta solicitação?"
        message="O afiliado fica sabendo que a solicitação foi rejeitada. Ele pode solicitar uma nova afiliação depois se quiser."
        confirmLabel="Rejeitar"
        variant="danger"
        isLoading={reject.isPending}
        onConfirm={() => rejectingId && reject.mutate(rejectingId)}
        onCancel={() => setRejectingId(null)}
      />
    </div>
  );
}

/**
 * Label curto da data relevante baseado no status — produtor escaneia a
 * lista e identifica quanto tempo cada estado existe (ex: "Solicitada há 3
 * dias" ajuda a priorizar PENDINGs antigas).
 */
function formatAffiliationDateLabel(a: Affiliation): string {
  const dateStr =
    a.status === "PENDING" ? a.requestedAt
    : a.status === "APPROVED" ? (a.approvedAt ?? a.requestedAt)
    : a.status === "REJECTED" ? (a.rejectedAt ?? a.requestedAt)
    : a.status === "REVOKED" ? (a.revokedAt ?? a.requestedAt)
    : a.requestedAt;
  if (!dateStr) return "";
  const verb =
    a.status === "PENDING" ? "Solicitada"
    : a.status === "APPROVED" ? "Aprovada"
    : a.status === "REJECTED" ? "Rejeitada"
    : a.status === "REVOKED" ? "Revogada"
    : "Criada";
  return `${verb} ${formatRelativeTime(dateStr)}`;
}

function formatAffiliationDateTooltip(a: Affiliation): string {
  const dateStr =
    a.status === "PENDING" ? a.requestedAt
    : a.status === "APPROVED" ? (a.approvedAt ?? a.requestedAt)
    : a.status === "REJECTED" ? (a.rejectedAt ?? a.requestedAt)
    : a.status === "REVOKED" ? (a.revokedAt ?? a.requestedAt)
    : a.requestedAt;
  if (!dateStr) return "";
  return new Date(dateStr).toLocaleString("pt-BR", {
    day: "2-digit", month: "2-digit", year: "numeric",
    hour: "2-digit", minute: "2-digit",
  });
}

/** Formato relativo curto — "há 3 dias", "há 2 meses", "agora". */
function formatRelativeTime(dateStr: string): string {
  const date = new Date(dateStr);
  const diffMs = Date.now() - date.getTime();
  const diffSec = Math.floor(diffMs / 1000);
  const diffMin = Math.floor(diffSec / 60);
  const diffHour = Math.floor(diffMin / 60);
  const diffDay = Math.floor(diffHour / 24);
  const diffMonth = Math.floor(diffDay / 30);
  const diffYear = Math.floor(diffDay / 365);
  if (diffSec < 60) return "agora";
  if (diffMin < 60) return `há ${diffMin} min`;
  if (diffHour < 24) return `há ${diffHour}h`;
  if (diffDay < 30) return `há ${diffDay} ${diffDay === 1 ? "dia" : "dias"}`;
  if (diffMonth < 12) return `há ${diffMonth} ${diffMonth === 1 ? "mês" : "meses"}`;
  return `há ${diffYear} ${diffYear === 1 ? "ano" : "anos"}`;
}

function AffiliationRow({
  a,
  onApprove,
  onReject,
  onRevoke,
}: {
  a: Affiliation;
  onApprove: () => void;
  onReject: () => void;
  onRevoke: () => void;
}) {
  // Row inteira clicável → /affiliations/[id] para produtor drillar nas
  // métricas individuais do afiliado (dashboard com cliques/vendas/conversão).
  // Mesma técnica das outras pages clicáveis: <Link absolute inset-0 z-0>
  // captura o clique em qualquer área "vazia"; conteúdo em z-10 com
  // pointer-events-none deixa o clique passar; botões em pointer-events-auto
  // re-habilitam para capturar ações próprias (approve/reject/revoke/copy).
  return (
    <li className="group relative">
      <Link
        href={`/affiliations/${a.id}`}
        className="absolute inset-0 z-0"
        aria-label={`Ver métricas de ${a.affiliateSellerName ?? "afiliado"}`}
      />
      <div className="relative z-10 flex items-center gap-3 px-5 py-4 group-hover:bg-gray-50/60 dark:group-hover:bg-white/[0.02] transition-colors pointer-events-none">
        {/* Avatar inicial — sumário visual do afiliado. 2 letras max,
            background brand-50/15 pra integrar com a identidade. */}
        <div className="w-9 h-9 rounded-full bg-brand-50 dark:bg-brand-500/15 flex items-center justify-center shrink-0 text-[11px] font-semibold text-brand-700 dark:text-brand-300 tabular-nums">
          {(a.affiliateSellerName ?? "?")
            .split(" ")
            .map((w) => w[0])
            .filter(Boolean)
            .slice(0, 2)
            .join("")
            .toUpperCase() || "?"}
        </div>

        <div className="min-w-0 flex-1">
          <div className="flex items-center gap-2 mb-1 flex-wrap">
            <p className="text-sm font-medium text-gray-900 dark:text-white group-hover:text-brand-600 dark:group-hover:text-brand-400 transition-colors">
              {a.affiliateSellerName ?? a.affiliateSellerId.slice(0, 8)}
            </p>
            <span className={`inline-flex items-center px-2 py-0.5 rounded-md text-[10px] uppercase tracking-wider font-semibold ${AFFILIATION_STATUS_CLS[a.status]}`}>
              {AFFILIATION_STATUS_LABEL[a.status]}
            </span>
          </div>
          <p className="text-xs text-gray-500 dark:text-gray-400 flex items-center gap-x-2 gap-y-0.5 flex-wrap">
            <span>
              {a.effectiveCommissionPercent.toFixed(1)}% de comissão
              {a.commissionPercent != null && (
                <span className="ml-1 inline-flex items-center px-1 py-0 rounded text-[9px] uppercase tracking-wider font-semibold bg-gray-100 text-gray-600 dark:bg-gray-800 dark:text-gray-400">
                  override
                </span>
              )}
            </span>
            <span className="text-gray-300 dark:text-gray-600">·</span>
            <span className="font-mono text-[11px] truncate" title={`Tracking: ${a.trackingCode}`}>
              {a.trackingCode}
            </span>
            <span className="text-gray-300 dark:text-gray-600">·</span>
            <span title={formatAffiliationDateTooltip(a)}>
              {formatAffiliationDateLabel(a)}
            </span>
          </p>
        </div>

        <div className="flex items-center gap-1.5 shrink-0 pointer-events-auto">
          {a.status === "PENDING" && (
            <>
              <button
                onClick={onApprove}
                className="h-8 rounded-lg bg-brand-500 hover:bg-brand-600 px-3 text-xs font-semibold text-white transition-colors"
              >
                Aprovar
              </button>
              <button
                onClick={onReject}
                className="h-8 rounded-lg px-3 text-xs font-medium text-gray-600 dark:text-gray-400 hover:bg-gray-100 dark:hover:bg-gray-800 transition-colors"
              >
                Rejeitar
              </button>
            </>
          )}
          {a.status === "APPROVED" && a.checkoutUrl && (
            <button
              onClick={() => { navigator.clipboard.writeText(resolveCheckoutUrl(a.checkoutUrl)); }}
              title={resolveCheckoutUrl(a.checkoutUrl)}
              className="h-8 rounded-lg border border-gray-200 dark:border-gray-700 bg-white dark:bg-gray-900 px-3 text-xs font-medium text-gray-700 dark:text-gray-300 hover:bg-gray-50 dark:hover:bg-gray-800 transition-colors"
            >
              Copiar link
            </button>
          )}
          {a.status === "APPROVED" && (
            <button
              onClick={onRevoke}
              className="h-8 rounded-lg px-3 text-xs font-medium text-error-700 dark:text-error-400 hover:bg-error-50 dark:hover:bg-error-500/10 transition-colors"
            >
              Revogar
            </button>
          )}
          <svg
            width="14" height="14" viewBox="0 0 24 24" fill="none"
            stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round"
            aria-hidden="true"
            className="text-gray-300 dark:text-gray-600 group-hover:text-gray-500 dark:group-hover:text-gray-400 transition-colors pointer-events-none ml-1"
          >
            <polyline points="9 18 15 12 9 6" />
          </svg>
        </div>
      </div>
    </li>
  );
}
