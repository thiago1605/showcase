import { api } from "@/lib/api/client";
import {
  AFFILIATION_MODE_BY_INDEX,
  AFFILIATION_STATUS_BY_INDEX,
  PRODUCT_STATUS_BY_INDEX,
  PRODUCT_TYPE_BY_INDEX,
} from "@/types";
import type {
  Affiliation,
  AffiliationList,
  AffiliationModeCode,
  AffiliationStatusCode,
  Product,
  ProductList,
  ProductMetrics,
  ProductOwnerStats,
  ProductStatusCode,
  ProductTypeCode,
} from "@/types";

// Backend serializa enums como int. Normalizamos pra union string igual
// fizemos pra SellerTier / NotificationType.

interface RawProductMetrics {
  sales30d: number;
  volume30d: number;
  activeAffiliates: number;
  salesByDay: number[];
}

interface RawProduct {
  id: string;
  ownerSellerId: string;
  ownerSellerName: string | null;
  name: string;
  slug: string;
  description: string | null;
  coverImageUrl: string | null;
  price: number;
  currency: string;
  type: number | string;
  deliveryUrl: string | null;
  defaultAffiliateCommissionPercent: number;
  affiliationMode: number | string;
  status: number | string;
  splitRuleId: string | null;
  category: string | null;
  facebookPixelId?: string | null;
  googleAdsConversionId?: string | null;
  createdAt: string;
  updatedAt: string;
  metrics?: RawProductMetrics | null;
  currentSellerAffiliationStatus?: number | string | null;
}

interface RawAffiliation {
  id: string;
  productId: string;
  productName: string | null;
  productSlug: string | null;
  productPrice: number | null;
  productCoverImageUrl?: string | null;
  affiliateSellerId: string;
  affiliateSellerName: string | null;
  status: number | string;
  commissionPercent: number | null;
  effectiveCommissionPercent: number;
  trackingCode: string;
  checkoutUrl: string;
  requestedAt: string;
  approvedAt: string | null;
  rejectedAt: string | null;
  revokedAt: string | null;
  rejectedReason: string | null;
  createdAt: string;
}

function normEnum<T extends string>(raw: unknown, table: T[]): T {
  if (typeof raw === "string") return raw as T;
  if (typeof raw === "number" && raw >= 0 && raw < table.length) return table[raw];
  return table[0];
}

function normalizeMetrics(r: RawProductMetrics | null | undefined): ProductMetrics | null {
  if (!r) return null;
  return {
    sales30d: r.sales30d,
    volume30d: r.volume30d,
    activeAffiliates: r.activeAffiliates,
    salesByDay: r.salesByDay ?? new Array(30).fill(0),
  };
}

function normalizeProduct(r: RawProduct): Product {
  return {
    ...r,
    type: normEnum<ProductTypeCode>(r.type, PRODUCT_TYPE_BY_INDEX),
    affiliationMode: normEnum<AffiliationModeCode>(
      r.affiliationMode,
      AFFILIATION_MODE_BY_INDEX,
    ),
    status: normEnum<ProductStatusCode>(r.status, PRODUCT_STATUS_BY_INDEX),
    metrics: normalizeMetrics(r.metrics),
    // Catálogo de afiliação populariza esse campo; demais endpoints retornam
    // null → mapeia para null.
    currentSellerAffiliationStatus:
      r.currentSellerAffiliationStatus == null
        ? null
        : normEnum<AffiliationStatusCode>(
            r.currentSellerAffiliationStatus,
            AFFILIATION_STATUS_BY_INDEX,
          ),
  };
}

function normalizeAffiliation(r: RawAffiliation): Affiliation {
  return {
    ...r,
    status: normEnum<AffiliationStatusCode>(
      r.status,
      AFFILIATION_STATUS_BY_INDEX,
    ),
  };
}

// Enums revertidos pro int do backend ao mandar request (já que o backend
// expecta o enum binding por nome OU int — usar nome string é mais robusto).
function enumIndex<T extends string>(value: T, table: T[]): number {
  const i = table.indexOf(value);
  return i < 0 ? 0 : i;
}

// ---- Products ----

export interface CreateProductInput {
  name: string;
  price: number;
  type: ProductTypeCode;
  defaultAffiliateCommissionPercent: number;
  affiliationMode: AffiliationModeCode;
  description?: string;
  coverImageUrl?: string;
  deliveryUrl?: string;
  splitRuleId?: string;
  category?: string;
  slug?: string;
}

export interface UpdateProductInput {
  name?: string;
  description?: string;
  coverImageUrl?: string;
  price?: number;
  deliveryUrl?: string;
  defaultAffiliateCommissionPercent?: number;
  affiliationMode?: AffiliationModeCode;
  splitRuleId?: string;
  category?: string;
  /** Backend trata string vazia como "remover" — set null no DB. */
  facebookPixelId?: string;
  googleAdsConversionId?: string;
}

export interface MarketplaceFilters {
  /**
   * Lista de categorias selecionadas (multi-select). Cada string mantém a
   * capitalização original cadastrada pelo produtor. Quando vazia/undefined,
   * o filtro de categoria é desabilitado.
   */
  categories?: string[];
  minPrice?: number;
  maxPrice?: number;
  mode?: AffiliationModeCode;
}

export const marketplaceService = {
  // Producer endpoints
  /**
   * Upload de capa do produto. Two-step: faz upload primeiro, recebe URL,
   * depois passa essa URL como `coverImageUrl` no create/update. Permite
   * trocar capa sem reenviar o resto do form, e dá preview imediato.
   *
   * Aceita PNG / JPEG / WEBP até 5 MB. Validação de MIME + magic bytes no
   * backend; aqui só fazemos o upload + retornamos a URL pública resultante.
   *
   * Usa `fetch` direto em vez do api client porque api.post serializa JSON —
   * upload é multipart/form-data, formato incompatível.
   */
  async uploadProductCover(file: File): Promise<string> {
    const form = new FormData();
    form.append("file", file);

    const baseUrl =
      process.env.NEXT_PUBLIC_API_BASE_URL || "http://localhost:5000";
    const token =
      typeof window !== "undefined"
        ? sessionStorage.getItem("fellow_access_token")
        : null;

    // IdempotencyMiddleware do backend exige header em todo POST de /api/v1.
    // O api client auto-injeta, mas este upload usa `fetch` direto pq multipart
    // não passa pelo JSON serializer do client. Geramos UUID per-call —
    // diferente por upload, então retry de network reaproveita o cache do
    // backend e o seller não vê arquivos duplicados se houver double-click.
    const headers: Record<string, string> = {
      "Idempotency-Key":
        typeof crypto !== "undefined" && crypto.randomUUID
          ? crypto.randomUUID()
          : `fp-${Date.now()}-${Math.random().toString(36).slice(2)}`,
    };
    if (token) headers["Authorization"] = `Bearer ${token}`;

    const response = await fetch(`${baseUrl}/api/v1/products/upload-cover`, {
      method: "POST",
      body: form,
      headers,
    });

    if (!response.ok) {
      // Tenta extrair mensagem amigável do backend. Formato esperado:
      // `{ success: false, message: "...", data: null, errors: [...] }` (StandardResponseFilter).
      // Alguns endpoints retornam ProblemDetails: `{ title, detail, status }`.
      // Outros legacy: `{ error: "..." }`. Cobrimos os 3 + erros do array.
      let message = "Erro ao fazer upload da imagem.";
      try {
        const body = await response.json();
        const fromErrorsArray =
          Array.isArray(body?.errors) && body.errors.length > 0
            ? typeof body.errors[0] === "string"
              ? body.errors[0]
              : body.errors[0]?.message
            : undefined;
        message =
          body?.message ||
          body?.detail ||
          body?.title ||
          body?.error ||
          fromErrorsArray ||
          message;
      } catch {
        /* não-JSON, mantém fallback */
      }
      throw new Error(message);
    }

    const raw = (await response.json()) as { success?: boolean; data?: { url: string }; url?: string };
    // Backend wrappa em ApiResponse { success, data, errors } — em alguns casos
    // (custom action result) volta { url } cru. Aceita ambos pra robustez.
    return raw.data?.url ?? raw.url ?? "";
  },

  async createProduct(input: CreateProductInput): Promise<Product> {
    const raw = await api.post<RawProduct>("/api/v1/products", {
      ...input,
      type: enumIndex(input.type, PRODUCT_TYPE_BY_INDEX),
      affiliationMode: enumIndex(input.affiliationMode, AFFILIATION_MODE_BY_INDEX),
    });
    return normalizeProduct(raw);
  },

  async updateProduct(id: string, input: UpdateProductInput): Promise<Product> {
    const raw = await api.patch<RawProduct>(`/api/v1/products/${id}`, {
      ...input,
      affiliationMode: input.affiliationMode
        ? enumIndex(input.affiliationMode, AFFILIATION_MODE_BY_INDEX)
        : undefined,
    });
    return normalizeProduct(raw);
  },

  async listMyProducts(params?: {
    status?: ProductStatusCode;
    page?: number;
    pageSize?: number;
  }): Promise<ProductList> {
    const qs = new URLSearchParams();
    if (params?.status) qs.set("status", String(enumIndex(params.status, PRODUCT_STATUS_BY_INDEX)));
    if (params?.page) qs.set("page", String(params.page));
    if (params?.pageSize) qs.set("pageSize", String(params.pageSize));
    const url = qs.toString() ? `/api/v1/products?${qs}` : "/api/v1/products";
    const raw = await api.get<{ items: RawProduct[]; totalCount: number }>(url);
    return { items: raw.items.map(normalizeProduct), totalCount: raw.totalCount };
  },

  async getProduct(id: string): Promise<Product> {
    const raw = await api.get<RawProduct>(`/api/v1/products/${id}`);
    return normalizeProduct(raw);
  },

  /**
   * Resumo agregado pros stats cards no topo de /products. Backend computa
   * em 2 queries (contagem por status + sum/count das TX 30d) — barato o
   * suficiente pra TanStack rodar com staleTime baixo e refresh on focus.
   */
  async getMyProductStats(days: number = 30): Promise<ProductOwnerStats> {
    return await api.get<ProductOwnerStats>(`/api/v1/products/stats?days=${days}`);
  },

  async publishProduct(id: string): Promise<Product> {
    const raw = await api.post<RawProduct>(`/api/v1/products/${id}/publish`, {});
    return normalizeProduct(raw);
  },
  async pauseProduct(id: string): Promise<Product> {
    const raw = await api.post<RawProduct>(`/api/v1/products/${id}/pause`, {});
    return normalizeProduct(raw);
  },
  async resumeProduct(id: string): Promise<Product> {
    const raw = await api.post<RawProduct>(`/api/v1/products/${id}/resume`, {});
    return normalizeProduct(raw);
  },
  async archiveProduct(id: string): Promise<Product> {
    const raw = await api.post<RawProduct>(`/api/v1/products/${id}/archive`, {});
    return normalizeProduct(raw);
  },

  // === Assets (materiais de divulgação) ===

  async listProductAssets(productId: string): Promise<ProductAsset[]> {
    return await api.get<ProductAsset[]>(`/api/v1/products/${productId}/assets`);
  },

  async addProductAsset(productId: string, input: {
    title: string;
    type: string;
    url: string;
    mimeType?: string;
    sizeBytes?: number;
  }): Promise<ProductAsset> {
    return await api.post<ProductAsset>(`/api/v1/products/${productId}/assets`, input);
  },

  async deleteProductAsset(assetId: string): Promise<void> {
    await api.delete<void>(`/api/v1/products/assets/${assetId}`);
  },

  // === Order Bumps (ofertas adicionais no checkout) ===

  /**
   * Lista bumps configurados num main product (painel do produtor — ativos+inativos).
   * Inclui dados do produto referenciado (nome, preço, cover, status) já join-ados
   * pra evitar N+1 no frontend.
   */
  async listOrderBumps(productId: string): Promise<OrderBump[]> {
    return await api.get<OrderBump[]>(`/api/v1/products/${productId}/order-bumps`);
  },

  async createOrderBump(productId: string, input: CreateOrderBumpInput): Promise<OrderBump> {
    return await api.post<OrderBump>(`/api/v1/products/${productId}/order-bumps`, input);
  },

  async updateOrderBump(
    productId: string,
    bumpId: string,
    input: UpdateOrderBumpInput,
  ): Promise<OrderBump> {
    return await api.put<OrderBump>(`/api/v1/products/${productId}/order-bumps/${bumpId}`, input);
  },

  async deleteOrderBump(productId: string, bumpId: string): Promise<void> {
    await api.delete<void>(`/api/v1/products/${productId}/order-bumps/${bumpId}`);
  },

  /**
   * Lista pública (sem auth) de bumps ativos pro slug do checkout. Backend filtra
   * bumps cujo produto referenciado não está PUBLISHED. Retorna lista vazia se
   * nenhum bump configurado, ou 404 se o slug não bate em produto publicado.
   */
  async getPublicOrderBumps(slug: string): Promise<PublicOrderBump[]> {
    return await api.get<PublicOrderBump[]>(`/api/v1/public/products/${slug}/order-bumps`);
  },

  /**
   * Leaderboard de afiliados do produto. Default 10. Só o produtor dono vê.
   */
  async getProductLeaderboard(productId: string, limit = 10): Promise<AffiliateLeaderboardEntry[]> {
    return await api.get<AffiliateLeaderboardEntry[]>(
      `/api/v1/products/${productId}/affiliates/leaderboard?limit=${limit}`,
    );
  },

  // === Coupons (cupons de desconto) ===

  async listCoupons(productId?: string): Promise<Coupon[]> {
    const qs = productId ? `?productId=${productId}` : "";
    return await api.get<Coupon[]>(`/api/v1/coupons${qs}`);
  },

  /**
   * Lista unificada do painel /coupons: globais do tenant + específicos dos
   * produtos do seller logado. Cada item tem `productName` quando aplicável.
   */
  async listMyCoupons(): Promise<Coupon[]> {
    return await api.get<Coupon[]>("/api/v1/coupons/mine");
  },

  async createCoupon(input: CreateCouponInput): Promise<Coupon> {
    return await api.post<Coupon>("/api/v1/coupons", input);
  },

  async deleteCoupon(id: string): Promise<void> {
    await api.delete<void>(`/api/v1/coupons/${id}`);
  },

  /** Valida cupom pra um slug específico. Retorna 404 se inválido. */
  async checkCoupon(slug: string, code: string): Promise<CouponValidation> {
    return await api.get<CouponValidation>(
      `/api/v1/public/products/${slug}/coupons/${encodeURIComponent(code)}/check`,
    );
  },

  async listProductAffiliations(productId: string, params?: {
    status?: AffiliationStatusCode;
    page?: number;
    pageSize?: number;
  }): Promise<AffiliationList> {
    const qs = new URLSearchParams();
    if (params?.status) qs.set("status", String(enumIndex(params.status, AFFILIATION_STATUS_BY_INDEX)));
    if (params?.page) qs.set("page", String(params.page));
    if (params?.pageSize) qs.set("pageSize", String(params.pageSize));
    const url = qs.toString()
      ? `/api/v1/products/${productId}/affiliations?${qs}`
      : `/api/v1/products/${productId}/affiliations`;
    const raw = await api.get<{ items: RawAffiliation[]; totalCount: number }>(url);
    return { items: raw.items.map(normalizeAffiliation), totalCount: raw.totalCount };
  },

  // Marketplace catalog (affiliate perspective)
  async listCatalog(params?: MarketplaceFilters & { page?: number; pageSize?: number }): Promise<ProductList> {
    const qs = new URLSearchParams();
    if (params?.categories && params.categories.length > 0) {
      // Backend recebe CSV: ?categories=Mentoria,Ebook
      qs.set("categories", params.categories.join(","));
    }
    if (params?.minPrice !== undefined) qs.set("minPrice", String(params.minPrice));
    if (params?.maxPrice !== undefined) qs.set("maxPrice", String(params.maxPrice));
    if (params?.mode) qs.set("mode", String(enumIndex(params.mode, AFFILIATION_MODE_BY_INDEX)));
    if (params?.page) qs.set("page", String(params.page));
    if (params?.pageSize) qs.set("pageSize", String(params.pageSize));
    const url = qs.toString()
      ? `/api/v1/marketplace/products?${qs}`
      : "/api/v1/marketplace/products";
    const raw = await api.get<{
      items: RawProduct[];
      totalCount: number;
      availableCategories?: { name: string; count: number }[] | null;
    }>(url);
    return {
      items: raw.items.map(normalizeProduct),
      totalCount: raw.totalCount,
      availableCategories: raw.availableCategories?.map((c) => ({
        name: c.name,
        count: c.count,
      })) ?? null,
    };
  },

  // Affiliations
  async requestAffiliation(productId: string): Promise<Affiliation> {
    const raw = await api.post<RawAffiliation>("/api/v1/affiliations", { productId });
    return normalizeAffiliation(raw);
  },

  /**
   * Resolve uma afiliação por id. Backend autoriza tanto o afiliado dono
   * quanto o produtor do produto — outros sellers do tenant recebem 404.
   * Substitui o fallback antigo de buscar via listMyAffiliations (que só
   * funcionava pro afiliado).
   */
  async getAffiliation(id: string): Promise<Affiliation> {
    const raw = await api.get<RawAffiliation>(`/api/v1/affiliations/${id}`);
    return normalizeAffiliation(raw);
  },

  async listMyAffiliations(params?: {
    status?: AffiliationStatusCode;
    page?: number;
    pageSize?: number;
  }): Promise<AffiliationList> {
    const qs = new URLSearchParams();
    if (params?.status) qs.set("status", String(enumIndex(params.status, AFFILIATION_STATUS_BY_INDEX)));
    if (params?.page) qs.set("page", String(params.page));
    if (params?.pageSize) qs.set("pageSize", String(params.pageSize));
    const url = qs.toString() ? `/api/v1/affiliations?${qs}` : "/api/v1/affiliations";
    const raw = await api.get<{ items: RawAffiliation[]; totalCount: number }>(url);
    return { items: raw.items.map(normalizeAffiliation), totalCount: raw.totalCount };
  },

  async approveAffiliation(id: string, overrideCommissionPercent?: number): Promise<Affiliation> {
    const raw = await api.post<RawAffiliation>(`/api/v1/affiliations/${id}/approve`, {
      overrideCommissionPercent,
    });
    return normalizeAffiliation(raw);
  },
  async rejectAffiliation(id: string, reason?: string): Promise<Affiliation> {
    const raw = await api.post<RawAffiliation>(`/api/v1/affiliations/${id}/reject`, { reason });
    return normalizeAffiliation(raw);
  },
  async revokeAffiliation(id: string, reason?: string): Promise<Affiliation> {
    const raw = await api.post<RawAffiliation>(`/api/v1/affiliations/${id}/revoke`, { reason });
    return normalizeAffiliation(raw);
  },

  /**
   * Métricas de performance da afiliação — TPV, ganhos, vendas em 30d/all-time.
   * Acessível pelo afiliado dono OU pelo produtor do produto. Backend retorna
   * 404 se a afiliação não existe; lança ApiError se não autorizado.
   */
  async getAffiliationStats(id: string, days: number = 30): Promise<AffiliateStats> {
    return await api.get<AffiliateStats>(`/api/v1/affiliations/${id}/stats?days=${days}`);
  },

  /**
   * Mini-stats de todas as afiliações do seller logado — pra enriquecer
   * a lista de /affiliations com sales/clicks/earnings 30d inline. Cache
   * mais agressivo no front (60s) pq agrega bastante coisa no backend.
   */
  async getMyAffiliationMiniStats(): Promise<AffiliateMiniStats[]> {
    return await api.get<AffiliateMiniStats[]>("/api/v1/affiliations/me/mini-stats");
  },

  // ---- Public checkout (sem auth) ----
  async resolvePublicProduct(slug: string, aff?: string): Promise<PublicProduct> {
    const qs = aff ? `?aff=${encodeURIComponent(aff)}` : "";
    return await api.get<PublicProduct>(`/api/v1/public/products/${slug}${qs}`);
  },

  async checkoutProduct(slug: string, body: PublicCheckoutRequest): Promise<PublicCheckoutResponse> {
    return await api.post<PublicCheckoutResponse>(
      `/api/v1/public/products/${slug}/checkout`,
      body,
    );
  },

  /**
   * Polling de status da TX criada pelo checkout. Usado pós-confirmação Stripe
   * (cartão) e pós-emissão Pix pra detectar captura. Retorna 404 se a TX não
   * pertence ao produto do slug.
   */
  async getMarketplaceTxStatus(slug: string, transactionId: string): Promise<PublicTxStatus> {
    return await api.get<PublicTxStatus>(
      `/api/v1/public/products/${slug}/transactions/${transactionId}/status`,
    );
  },

  /**
   * Registra um clique no link de divulgação de afiliação. Fire-and-forget no
   * mount de /p/[slug]?aff={code}. Dedup interno por fingerprint+afiliação em
   * janela 1h — refresh / volta repetida não infla a métrica. Backend sempre
   * retorna 204, mesmo se trackingCode for inválido (não vaza info).
   */
  async trackAffiliateClick(trackingCode: string): Promise<void> {
    await api.post<void>(`/api/v1/public/affiliates/${trackingCode}/click`, {});
  },
};

export interface PublicProduct {
  id: string;
  name: string;
  slug: string;
  description: string | null;
  coverImageUrl: string | null;
  price: number;
  currency: string;
  type: number;
  category: string | null;
  producerName: string | null;
  affiliate: {
    trackingCode: string;
    affiliateName: string | null;
    commissionPercent: number;
  } | null;
  /** Facebook Pixel ID configurado pelo produtor — frontend injeta script + dispara Purchase event. */
  facebookPixelId: string | null;
  /** Google Ads conversion id/label (formato "AW-XXX/YYY") — frontend injeta gtag + dispara event. */
  googleAdsConversionId: string | null;
}

export interface PublicCheckoutRequest {
  paymentType: number; // 0=CREDIT_CARD 1=DEBIT_CARD 2=PIX 3=BOLETO
  payerName?: string;
  payerDocument?: string;
  payerEmail?: string;
  payerPhone?: string;
  trackingCode?: string;
  installments?: number;
  /** Código do cupom aplicado — backend valida e aplica desconto. */
  couponCode?: string;
  // === UTM / tracking de origem ===
  // Capturados no /p/[slug] da URL + localStorage. Backend persiste em
  // Transaction.Metadata pra agregação posterior (atribuição de campanhas).
  utmSource?: string;
  utmMedium?: string;
  utmCampaign?: string;
  utmContent?: string;
  utmTerm?: string;
  gclid?: string;
  fbclid?: string;
  referrer?: string;
  /**
   * Ids dos order bumps selecionados pelo buyer. Cada GUID precisa bater num
   * bump ativo do produto principal — backend ignora inválidos silenciosamente
   * (não bloqueia a compra). Cada bump válido adiciona seu preço próprio ao
   * total cobrado e vira um item separado na TX.
   */
  bumpProductIds?: string[];
}

// Resposta mínima — payment instructions detalhadas dependem do tipo.
// Pra MVP: id da TX + status + (se PIX) o qrcode/copyPaste.
//
// O backend retorna `TransactionResponseDto` que tem `internalId`, `status`,
// `amount`, e um sub-objeto `payment` com os campos provider-specific. Espelhamos
// flatten aqui pra simplicidade no front; cartão precisa do `clientSecret` +
// `connectedAccountId` pra Stripe.confirmPayment funcionar com destination charge.
export interface PublicCheckoutResponse {
  internalId: string;
  status: string | number;
  amount: number;
  payment: {
    transactionId: string;
    pixQrCode?: string;
    pixImageUrl?: string;
    boletoUrl?: string;
    clientSecret?: string;
    connectedAccountId?: string;
  };
}

export interface PublicTxStatus {
  id: string;
  /** Nome string do enum TransactionStatus — "CAPTURED" / "FAILED" / "PROCESSING" / etc. */
  status: string;
  /** True quando não muda mais (CAPTURED, FAILED, VOIDED, REFUNDED, DECLINED). */
  isTerminal: boolean;
}

export interface Coupon {
  id: string;
  productId: string | null;
  /** Nome do produto quando productId != null. Null em cupons globais. */
  productName: string | null;
  code: string;
  /** 0=PERCENT, 1=FIXED */
  type: number;
  value: number;
  validFrom: string | null;
  validUntil: string | null;
  maxUses: number | null;
  usedCount: number;
  createdAt: string;
}

export interface CreateCouponInput {
  code: string;
  type: 0 | 1; // PERCENT | FIXED
  value: number;
  productId?: string;
  validFrom?: string;
  validUntil?: string;
  maxUses?: number;
}

export interface CouponValidation {
  code: string;
  type: number;
  value: number;
  discountAmount: number;
  finalPrice: number;
}

export interface AffiliateLeaderboardEntry {
  affiliationId: string;
  affiliateSellerId: string;
  affiliateName: string | null;
  salesCount: number;
  tpv: number;
  earnings: number;
  rank: number;
}

export interface ProductAsset {
  id: string;
  productId: string;
  title: string;
  type: string;
  url: string;
  mimeType: string | null;
  sizeBytes: number | null;
  createdAt: string;
}

export interface AffiliateMiniStats {
  affiliationId: string;
  sales30d: number;
  clicks30d: number;
  earnings30d: number;
}

export interface AffiliateStats {
  affiliationId: string;
  productId: string;
  productName: string;
  /** Janela em dias (7/30/90) das métricas abaixo. */
  periodDays: number;
  salesInPeriod: number;
  tpvInPeriod: number;
  earningsInPeriod: number;
  salesAllTime: number;
  tpvAllTime: number;
  earningsAllTime: number;
  earningsPending: number;
  clicksInPeriod: number;
  clicksAllTime: number;
  /** Vendas / clicks * 100. Null quando clicks == 0 (sem dado pra calcular). */
  conversionPercentInPeriod: number | null;
  conversionPercentAllTime: number | null;
  /** Arrays de `periodDays` ints, índice 0 = (days-1) atrás, último = hoje. */
  clicksByDay: number[];
  salesByDay: number[];
  /** Janela imediatamente anterior (mesma duração) — pra calcular delta. */
  previousSalesInPeriod: number;
  previousTpvInPeriod: number;
  previousEarningsInPeriod: number;
  previousClicksInPeriod: number;
}

// === Order Bumps ===

/**
 * Representação admin (painel do produtor) de um bump configurado. Inclui
 * snapshot do produto referenciado (nome/preço/cover/status) join-ado pelo
 * backend pra evitar N+1.
 */
export interface OrderBump {
  id: string;
  mainProductId: string;
  bumpProductId: string;
  bumpProductName: string;
  bumpProductPrice: number;
  bumpProductCoverImageUrl: string | null;
  /** Status do produto referenciado — int do enum (0=DRAFT,1=PUBLISHED,2=PAUSED,3=ARCHIVED). UI alerta se != 1. */
  bumpProductStatus: number;
  customTitle: string;
  customDescription: string | null;
  displayOrder: number;
  isActive: boolean;
  createdAt: string;
  updatedAt: string;
  /** Desconto absoluto (R$) aplicado ao bump no checkout. 0 = sem desconto. */
  discountAmount: number;
}

export interface CreateOrderBumpInput {
  bumpProductId: string;
  customTitle: string;
  customDescription?: string;
  displayOrder?: number;
  /** Desconto em R$ aplicado quando o cliente marca o bump. Default 0. */
  discountAmount?: number;
}

export interface UpdateOrderBumpInput {
  customTitle?: string;
  customDescription?: string;
  displayOrder?: number;
  isActive?: boolean;
  discountAmount?: number;
}

/** Representação pública (anônima) — usada pelo /p/[slug] pra renderizar bump cards. */
export interface PublicOrderBump {
  id: string;
  bumpProductId: string;
  title: string;
  description: string | null;
  /** Preço cheio do produto-bump (referência para o strikethrough). */
  price: number;
  currency: string;
  coverImageUrl: string | null;
  displayOrder: number;
  /** Desconto em R$ aplicado ao bump no checkout. 0 = sem desconto. */
  discountAmount: number;
  /** Preço final cobrado se o cliente marcar o bump = price - discountAmount. */
  finalPrice: number;
}
