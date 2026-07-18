"use client";

import { use, useEffect, useMemo, useRef, useState } from "react";
import Script from "next/script";
import { useSearchParams } from "next/navigation";
import { loadStripe, type Stripe } from "@stripe/stripe-js";
import {
  Elements,
  PaymentElement,
  useElements,
  useStripe,
} from "@stripe/react-stripe-js";
import { marketplaceService } from "@/services/marketplace.service";
import { paymentLinksService } from "@/services/payment-links.service";
import { Illustration } from "@/components/ui/Illustration";
import type {
  PublicCheckoutRequest,
  PublicCheckoutResponse,
  PublicOrderBump,
  PublicProduct,
} from "@/services/marketplace.service";

/**
 * Página pública de checkout do produto (modelo Kirvano). SEM auth — qualquer
 * cliente pode acessar.
 *
 * Atribuição de afiliado:
 *  1. Query param `?aff={trackingCode}` é prioridade (last-click clássico)
 *  2. Se ausente, fallback pra localStorage (`marketplace_aff_{slug}`) — cobre
 *     o cenário "afiliado mandou link, cliente abriu, fechou, voltou direto"
 *  3. Quando `?aff` está presente, GRAVA no localStorage com expiração 30 dias
 *
 * UTM / tracking de origem:
 *  - Capturados na URL na mesma sessão (utm_source, utm_medium, utm_campaign,
 *    utm_content, utm_term, gclid, fbclid).
 *  - Persistidos em localStorage por 30 dias (mesma janela do aff) pra
 *    atribuição consistente quando user reabre o link sem UTM.
 *  - Enviados no checkout → Transaction.Metadata pra agregação posterior.
 *
 * Pagamento: PIX como caminho primário (instant capture, simplest UX).
 * Cartão/Boleto entram em iteração seguinte (precisa de checkout Stripe Elements
 * ou redirect — fora do escopo do MVP do marketplace).
 */

const PAYMENT_TYPE = { CREDIT_CARD: 0, DEBIT_CARD: 1, PIX: 2, BOLETO: 3 };
const ATTRIBUTION_WINDOW_DAYS = 30;

/**
 * Chaves UTM + click IDs que capturamos. Tipado pra TS não deixar inconsistência
 * passar entre URL → storage → request. Strings normalizadas (lowercase, sem
 * acento) pra match com o que campaigns reais setam.
 */
type TrackingParams = {
  utm_source?: string;
  utm_medium?: string;
  utm_campaign?: string;
  utm_content?: string;
  utm_term?: string;
  gclid?: string;
  fbclid?: string;
  referrer?: string;
};

const UTM_KEYS = [
  "utm_source",
  "utm_medium",
  "utm_campaign",
  "utm_content",
  "utm_term",
  "gclid",
  "fbclid",
] as const;

function formatBRL(v: number) {
  return v.toLocaleString("pt-BR", { style: "currency", currency: "BRL" });
}

function readStoredAff(slug: string): string | null {
  if (typeof window === "undefined") return null;
  try {
    const raw = window.localStorage.getItem(`marketplace_aff_${slug}`);
    if (!raw) return null;
    const parsed = JSON.parse(raw) as { code: string; expiresAt: number };
    if (Date.now() > parsed.expiresAt) {
      window.localStorage.removeItem(`marketplace_aff_${slug}`);
      return null;
    }
    return parsed.code;
  } catch {
    return null;
  }
}

function persistAff(slug: string, code: string) {
  if (typeof window === "undefined") return;
  const expiresAt = Date.now() + ATTRIBUTION_WINDOW_DAYS * 24 * 60 * 60 * 1000;
  try {
    window.localStorage.setItem(
      `marketplace_aff_${slug}`,
      JSON.stringify({ code, expiresAt }),
    );
  } catch {
    /* private mode etc — ignora */
  }
}

/**
 * Lê UTMs persistidos por produto. Mesma janela de 30 dias que `aff` — se a
 * compra acontecer dentro desse prazo após o clique original, mantemos a
 * atribuição mesmo que o user tenha fechado a aba/voltado direto sem UTM.
 */
function readStoredTracking(slug: string): TrackingParams {
  if (typeof window === "undefined") return {};
  try {
    const raw = window.localStorage.getItem(`marketplace_utm_${slug}`);
    if (!raw) return {};
    const parsed = JSON.parse(raw) as { tracking: TrackingParams; expiresAt: number };
    if (Date.now() > parsed.expiresAt) {
      window.localStorage.removeItem(`marketplace_utm_${slug}`);
      return {};
    }
    return parsed.tracking ?? {};
  } catch {
    return {};
  }
}

function persistTracking(slug: string, tracking: TrackingParams) {
  if (typeof window === "undefined") return;
  if (Object.values(tracking).every((v) => !v)) return; // nada pra persistir
  const expiresAt = Date.now() + ATTRIBUTION_WINDOW_DAYS * 24 * 60 * 60 * 1000;
  try {
    window.localStorage.setItem(
      `marketplace_utm_${slug}`,
      JSON.stringify({ tracking, expiresAt }),
    );
  } catch {
    /* private mode etc — ignora */
  }
}

/**
 * Merge: URL params são prioridade (last-touch attribution clássico). Se uma
 * chave veio da URL, sobrescreve o que estava em storage. Senão, mantém o
 * valor antigo. `referrer` é capturado uma vez na primeira visita e não
 * sobrescreve em refresh.
 */
function mergeTracking(fromUrl: TrackingParams, fromStorage: TrackingParams): TrackingParams {
  const result: TrackingParams = { ...fromStorage };
  for (const key of UTM_KEYS) {
    const urlValue = fromUrl[key];
    if (urlValue) result[key] = urlValue;
  }
  if (fromUrl.referrer && !result.referrer) result.referrer = fromUrl.referrer;
  return result;
}

export default function PublicProductPage({
  params,
}: {
  params: Promise<{ slug: string }>;
}) {
  const { slug } = use(params);
  const searchParams = useSearchParams();
  const affFromUrl = searchParams.get("aff");

  const [product, setProduct] = useState<PublicProduct | null>(null);
  const [loadError, setLoadError] = useState<string | null>(null);

  // Order bumps disponíveis pro checkout + ids selecionados pelo buyer. Bumps
  // são fetched em paralelo com o resolve do produto — lista vazia = produto
  // sem bumps configurados (UI esconde a seção). Seleção é state local; só vai
  // pro backend no momento do submit.
  const [orderBumps, setOrderBumps] = useState<PublicOrderBump[]>([]);
  const [selectedBumpIds, setSelectedBumpIds] = useState<string[]>([]);

  // Estado do checkout
  const [paymentMethod, setPaymentMethod] = useState<"PIX" | "CARTAO" | "BOLETO">("PIX");
  const [payerName, setPayerName] = useState("");
  const [payerEmail, setPayerEmail] = useState("");
  const [payerDocument, setPayerDocument] = useState("");
  const [couponCode, setCouponCode] = useState("");
  const [couponValidation, setCouponValidation] = useState<{
    discountAmount: number;
    finalPrice: number;
  } | null>(null);
  const [couponError, setCouponError] = useState<string | null>(null);
  const [submitting, setSubmitting] = useState(false);
  const [checkoutError, setCheckoutError] = useState<string | null>(null);
  const [checkoutResult, setCheckoutResult] = useState<PublicCheckoutResponse | null>(null);

  // Atribuição: URL > localStorage. Quando vier da URL, persiste.
  const [resolvedAff, setResolvedAff] = useState<string | null>(null);
  const [resolvedTracking, setResolvedTracking] = useState<TrackingParams>({});

  // Stripe.js carregado lazy quando user seleciona Cartão. Mantém o page
  // load inicial leve (~80KB do bundle Stripe não entra se user paga via PIX).
  // PK vem do backend (/checkout/config) — mesmo pattern do /pay/[token].
  // Evita precisar de env var pública NEXT_PUBLIC_STRIPE_PUBLISHABLE_KEY
  // separada, e a chave pode trocar por ambiente sem rebuild do frontend.
  const [stripePromise, setStripePromise] = useState<Promise<Stripe | null> | null>(null);
  useEffect(() => {
    if (paymentMethod !== "CARTAO" || stripePromise) return;
    let cancelled = false;
    (async () => {
      try {
        const cfg = await paymentLinksService.getCheckoutConfig();
        if (cancelled) return;
        if (!cfg.stripePk) {
          setCheckoutError("Pagamento por cartão indisponível neste ambiente.");
          return;
        }
        setStripePromise(loadStripe(cfg.stripePk));
      } catch {
        if (!cancelled) setCheckoutError("Não foi possível inicializar o cartão.");
      }
    })();
    return () => { cancelled = true; };
  }, [paymentMethod, stripePromise]);

  // Polling de status pós-confirmação (cartão e PIX). Roda quando temos
  // checkoutResult com internalId e o status ainda não é terminal. Backend
  // expõe GET /api/v1/public/products/{slug}/transactions/{id}/status.
  // Mantemos no parent pq o estado de "captured" precisa ser refletido em
  // toda a árvore — o panel exibe vista de sucesso quando capturado.
  const [pollStatus, setPollStatus] = useState<string | null>(null);

  // Pixel firing — controla pra disparar Purchase só uma vez por TX capturada.
  // Sem isso, polling pós-captura re-dispararia o evento a cada refresh ou
  // tick. Ref em vez de state pq não precisamos re-renderizar quando dispara.
  const purchaseFiredRef = useRef(false);
  useEffect(() => {
    if (pollStatus !== "CAPTURED" || !product || !checkoutResult || purchaseFiredRef.current) return;
    purchaseFiredRef.current = true;

    const value = checkoutResult.amount ?? product.price;

    // Facebook Pixel: track Purchase. fbq pode não estar carregado se o produto
    // não tem pixel — ignora silently. Pixel ID já foi setado no init via
    // <Script> abaixo.
    try {
      const fbq = (window as unknown as { fbq?: (...args: unknown[]) => void }).fbq;
      if (product.facebookPixelId && typeof fbq === "function") {
        fbq("track", "Purchase", { value, currency: "BRL", content_name: product.name });
      }
    } catch {
      /* não-crítico */
    }

    // Google Ads conversion event via gtag. Formato esperado de send_to:
    // "AW-XXX/YYY" — produtor configura no Product.GoogleAdsConversionId.
    try {
      const gtag = (window as unknown as { gtag?: (...args: unknown[]) => void }).gtag;
      if (product.googleAdsConversionId && typeof gtag === "function") {
        gtag("event", "conversion", {
          send_to: product.googleAdsConversionId,
          value,
          currency: "BRL",
          transaction_id: checkoutResult.internalId,
        });
      }
    } catch {
      /* não-crítico */
    }
  }, [pollStatus, product, checkoutResult]);
  useEffect(() => {
    if (!checkoutResult?.internalId) return;
    if (pollStatus === "CAPTURED" || pollStatus === "FAILED" ||
        pollStatus === "VOIDED" || pollStatus === "REFUNDED" || pollStatus === "DECLINED") return;

    let cancelled = false;
    let attempts = 0;
    const MAX_ATTEMPTS = 150; // ~10 min @ 4s
    const tick = async () => {
      if (cancelled || attempts >= MAX_ATTEMPTS) return;
      attempts++;
      try {
        const res = await marketplaceService.getMarketplaceTxStatus(slug, checkoutResult.internalId);
        if (cancelled) return;
        setPollStatus(res.status);
        if (res.isTerminal) return;
      } catch {
        /* erro transitório — tenta de novo no próximo tick */
      }
      setTimeout(tick, 4000);
    };
    const timer = setTimeout(tick, 2000);
    return () => {
      cancelled = true;
      clearTimeout(timer);
    };
  }, [checkoutResult?.internalId, pollStatus, slug]);

  useEffect(() => {
    let initialAff: string | null = null;
    if (affFromUrl && affFromUrl.trim()) {
      initialAff = affFromUrl.trim();
      persistAff(slug, initialAff);
    } else {
      initialAff = readStoredAff(slug);
    }
    setResolvedAff(initialAff);

    // Extrai UTM + tracking da URL atual. document.referrer só faz sentido na
    // primeira visita — se o user navegou internamente, vai vir vazio
    // ou apontando pra própria página.
    const fromUrl: TrackingParams = {};
    for (const key of UTM_KEYS) {
      const v = searchParams.get(key);
      if (v && v.trim()) fromUrl[key] = v.trim().slice(0, 200);
    }
    if (typeof document !== "undefined" && document.referrer) {
      try {
        const ref = new URL(document.referrer);
        // Só guarda referrer externo (não auto-referência do próprio domínio)
        if (ref.host && ref.host !== window.location.host) {
          fromUrl.referrer = document.referrer.slice(0, 500);
        }
      } catch {
        /* referrer mal formado — ignora */
      }
    }
    const merged = mergeTracking(fromUrl, readStoredTracking(slug));
    persistTracking(slug, merged);
    setResolvedTracking(merged);

    let cancelled = false;
    marketplaceService
      .resolvePublicProduct(slug, initialAff ?? undefined)
      .then((p) => {
        if (cancelled) return;
        setProduct(p);
        // Se o produto carregou COM dados de afiliado (= o trackingCode passado
        // é válido e ativo), dispara o tracking de click. Fire-and-forget: erro
        // silencioso, não trava a UX. Dedup é feito server-side (fingerprint
        // + janela 1h), então safe pra disparar em refresh/navegação.
        if (p.affiliate?.trackingCode) {
          marketplaceService.trackAffiliateClick(p.affiliate.trackingCode).catch(() => {
            /* silent — não vale poluir console; dedup garante consistência */
          });
        }
      })
      .catch((err) => {
        if (cancelled) return;
        setLoadError(
          err instanceof Error ? err.message : "Produto não encontrado.",
        );
      });

    // Carrega order bumps em paralelo — endpoint anônimo separado. Falha silenciosa:
    // se 404 ou erro de rede, simplesmente não mostra bumps (não bloqueia checkout).
    marketplaceService
      .getPublicOrderBumps(slug)
      .then((bumps) => {
        if (cancelled) return;
        setOrderBumps(bumps);
      })
      .catch(() => {
        /* silent — bumps opcionais não bloqueiam checkout */
      });
    return () => { cancelled = true; };
  }, [slug, affFromUrl, searchParams]);

  /**
   * Monta o payload base de checkout — comum aos 3 métodos. PaymentType é
   * adicionado pelo caller pra cada método. UTM/aff persistidos em Metadata
   * pelo backend.
   */
  function buildCheckoutBody(method: "PIX" | "CARTAO" | "BOLETO") {
    const paymentType =
      method === "PIX" ? PAYMENT_TYPE.PIX :
      method === "CARTAO" ? PAYMENT_TYPE.CREDIT_CARD :
      PAYMENT_TYPE.BOLETO;
    return {
      paymentType,
      payerName: payerName.trim() || undefined,
      payerEmail: payerEmail.trim() || undefined,
      payerDocument: payerDocument.replace(/\D/g, "") || undefined,
      trackingCode: resolvedAff ?? undefined,
      // Cupom só é enviado se foi validado com sucesso — evita rejeição
      // silenciosa no backend de cupom inválido (que não bloqueia compra mas
      // também não dá desconto).
      couponCode: couponValidation ? couponCode.trim() : undefined,
      utmSource: resolvedTracking.utm_source,
      utmMedium: resolvedTracking.utm_medium,
      utmCampaign: resolvedTracking.utm_campaign,
      utmContent: resolvedTracking.utm_content,
      utmTerm: resolvedTracking.utm_term,
      gclid: resolvedTracking.gclid,
      fbclid: resolvedTracking.fbclid,
      referrer: resolvedTracking.referrer,
      // Order bumps selecionados — backend valida cada id contra os bumps
      // ativos do produto. Inválidos são ignorados silenciosamente (não
      // bloqueia compra). Cada bump válido adiciona seu próprio preço ao total
      // cobrado e vira um TransactionItem separado.
      bumpProductIds: selectedBumpIds.length > 0 ? selectedBumpIds : undefined,
    };
  }

  async function applyCoupon() {
    if (!couponCode.trim()) return;
    setCouponError(null);
    try {
      const val = await marketplaceService.checkCoupon(slug, couponCode.trim());
      setCouponValidation({ discountAmount: val.discountAmount, finalPrice: val.finalPrice });
    } catch {
      setCouponError("Cupom inválido ou expirado.");
      setCouponValidation(null);
    }
  }

  function removeCoupon() {
    setCouponCode("");
    setCouponValidation(null);
    setCouponError(null);
  }

  /**
   * Handler de submit pra PIX e Boleto — ambos: criar TX → mostrar instruções.
   * Cartão tem fluxo próprio dentro do `<StripeCardCheckoutPanel>` pq precisa
   * do step extra de `stripe.confirmPayment()` após o backend retornar
   * o clientSecret.
   */
  async function handleNonCardSubmit(e: React.FormEvent) {
    e.preventDefault();
    if (!product) return;
    if (paymentMethod === "CARTAO") return; // cartão tem flow próprio, não passa aqui
    setCheckoutError(null);
    setSubmitting(true);
    try {
      const result = await marketplaceService.checkoutProduct(slug, buildCheckoutBody(paymentMethod));
      setCheckoutResult(result);
    } catch (err) {
      setCheckoutError(err instanceof Error ? err.message : "Erro ao iniciar pagamento.");
    } finally {
      setSubmitting(false);
    }
  }

  if (loadError) {
    return (
      <Shell>
        <div className="text-center py-12">
          <p className="text-base font-medium text-gray-800 dark:text-gray-200">
            Produto indisponível
          </p>
          <p className="text-sm text-gray-500 dark:text-gray-400 mt-1">{loadError}</p>
        </div>
      </Shell>
    );
  }

  if (!product) {
    return (
      <Shell>
        <div className="animate-pulse space-y-4">
          <div className="aspect-[16/9] w-full bg-gray-200 dark:bg-gray-800 rounded-2xl" />
          <div className="h-6 w-2/3 bg-gray-200 dark:bg-gray-800 rounded" />
          <div className="h-4 w-full bg-gray-100 dark:bg-gray-700 rounded" />
        </div>
      </Shell>
    );
  }

  // Preço base após cupom (se aplicado). Bumps somam por cima sem desconto —
  // política igual ao backend: cupom incide só no produto principal pra evitar
  // que cupons de 100% drenem o valor dos bumps acidentalmente.
  const mainPriceAfterCoupon = couponValidation
    ? couponValidation.finalPrice
    : product.price;
  const selectedBumps = orderBumps.filter((b) => selectedBumpIds.includes(b.id));
  // Cada bump cobra o preço com desconto aplicado (finalPrice = price - discount).
  // Fallback para price caso o backend ainda não tenha emitido finalPrice
  // (migration old → new): garante continuidade durante deploys.
  const bumpsTotal = selectedBumps.reduce(
    (acc, b) => acc + (b.finalPrice ?? b.price),
    0,
  );
  const totalAmount = mainPriceAfterCoupon + bumpsTotal;

  function toggleBump(bumpId: string) {
    setSelectedBumpIds((prev) =>
      prev.includes(bumpId) ? prev.filter((id) => id !== bumpId) : [...prev, bumpId],
    );
  }

  // Pós-checkout: mostra QR PIX / boleto / status do cartão
  if (checkoutResult) {
    return (
      <Shell>
        <CheckoutResultPanel
          result={checkoutResult}
          productName={product.name}
          pollStatus={pollStatus}
        />
      </Shell>
    );
  }

  return (
    <Shell>
      {/* Pixel/Tracking scripts injetados condicionalmente. Só renderizam
          quando o produto tem id configurado — sem id, nenhum script é incluído
          (evita bundle de tracking pra produtos que não configuraram nada).
          `afterInteractive` posiciona depois do hydrate sem bloquear LCP. */}
      {product.facebookPixelId && (
        <Script id="fb-pixel" strategy="afterInteractive">{`
          !function(f,b,e,v,n,t,s){if(f.fbq)return;n=f.fbq=function(){n.callMethod?n.callMethod.apply(n,arguments):n.queue.push(arguments)};if(!f._fbq)f._fbq=n;n.push=n;n.loaded=!0;n.version='2.0';n.queue=[];t=b.createElement(e);t.async=!0;t.src=v;s=b.getElementsByTagName(e)[0];s.parentNode.insertBefore(t,s)}(window,document,'script','https://connect.facebook.net/en_US/fbevents.js');
          fbq('init','${product.facebookPixelId}');
          fbq('track','PageView');
        `}</Script>
      )}
      {product.googleAdsConversionId && (
        <>
          <Script
            id="gtag-loader"
            strategy="afterInteractive"
            src={`https://www.googletagmanager.com/gtag/js?id=${product.googleAdsConversionId.split("/")[0]}`}
          />
          <Script id="gtag-init" strategy="afterInteractive">{`
            window.dataLayer = window.dataLayer || [];
            function gtag(){dataLayer.push(arguments);}
            window.gtag = gtag;
            gtag('js', new Date());
            gtag('config', '${product.googleAdsConversionId.split("/")[0]}');
          `}</Script>
        </>
      )}

      <div className="grid grid-cols-1 lg:grid-cols-[1fr_400px] gap-8 items-start">
        <div>
          {product.coverImageUrl ? (
            // eslint-disable-next-line @next/next/no-img-element
            <img
              src={product.coverImageUrl}
              alt={product.name}
              className="aspect-[16/9] w-full object-cover rounded-2xl"
            />
          ) : (
            <div className="aspect-[16/9] w-full bg-gradient-to-br from-brand-500/20 via-brand-500/10 to-purple-500/20 rounded-2xl" />
          )}

          <div className="mt-6">
            <div className="flex items-center gap-2 mb-2">
              {product.category && (
                <span className="text-[11px] uppercase tracking-wider text-gray-500 dark:text-gray-400">
                  {product.category}
                </span>
              )}
            </div>
            <h1 className="text-2xl font-semibold text-gray-900 dark:text-white">
              {product.name}
            </h1>
            <p className="text-sm text-gray-500 dark:text-gray-400 mt-1">
              Por {product.producerName ?? "Produtor"}
            </p>
            {product.description && (
              <div className="mt-4 prose-sm prose-gray dark:prose-invert text-gray-700 dark:text-gray-300 whitespace-pre-wrap">
                {product.description}
              </div>
            )}
          </div>

          {/* Order bumps section — ofertas adicionais. Aparece logo após o
              card do produto principal. Cada bump é um card horizontal com
              checkbox + cover + título custom + preço. Toggle atualiza state
              local e reflete no breakdown + botão de pagamento. */}
          {orderBumps.length > 0 && (
            <div className="mt-8">
              <p className="text-sm font-semibold text-gray-900 dark:text-white mb-3">
                Aproveite e leve também
              </p>
              <ul className="space-y-3">
                {orderBumps.map((bump) => {
                  const selected = selectedBumpIds.includes(bump.id);
                  return (
                    <li key={bump.id}>
                      <label
                        className={`flex items-start gap-4 rounded-2xl border-2 p-4 cursor-pointer transition-colors ${
                          selected
                            ? "border-brand-500 bg-brand-50/40 dark:bg-brand-500/10"
                            : "border-gray-200 dark:border-gray-800 hover:border-gray-300 dark:hover:border-gray-700"
                        }`}
                      >
                        <input
                          type="checkbox"
                          checked={selected}
                          onChange={() => toggleBump(bump.id)}
                          className="mt-1 h-5 w-5 rounded border-gray-300 text-brand-500 focus:ring-brand-500"
                        />
                        <div className="h-16 w-20 shrink-0 rounded-md overflow-hidden bg-gray-100 dark:bg-gray-800">
                          {bump.coverImageUrl ? (
                            // eslint-disable-next-line @next/next/no-img-element
                            <img
                              src={bump.coverImageUrl}
                              alt={bump.title}
                              className="h-full w-full object-cover"
                            />
                          ) : (
                            <div className="h-full w-full bg-gradient-to-br from-brand-500/20 to-purple-500/20" />
                          )}
                        </div>
                        <div className="flex-1 min-w-0">
                          <p className="text-sm font-medium text-gray-900 dark:text-white">
                            {bump.title}
                          </p>
                          {bump.description && (
                            <p className="text-[11px] text-gray-500 dark:text-gray-400 mt-1 line-clamp-2">
                              {bump.description}
                            </p>
                          )}
                          <div className="mt-2 flex items-baseline gap-1.5 flex-wrap">
                            {bump.discountAmount > 0 ? (
                              <>
                                <span className="text-xs text-gray-400 dark:text-gray-500 line-through tabular-nums">
                                  {formatBRL(bump.price)}
                                </span>
                                <span className="text-sm font-semibold text-success-700 dark:text-success-400 tabular-nums">
                                  + {formatBRL(bump.finalPrice ?? bump.price)}
                                </span>
                                <span className="inline-flex items-center px-1.5 py-0.5 rounded text-[9px] uppercase tracking-wider font-bold bg-success-50 text-success-700 dark:bg-success-500/15 dark:text-success-400">
                                  −{formatBRL(bump.discountAmount)}
                                </span>
                              </>
                            ) : (
                              <span className="text-sm font-semibold text-brand-600 dark:text-brand-400 tabular-nums">
                                + {formatBRL(bump.price)}
                              </span>
                            )}
                          </div>
                        </div>
                      </label>
                    </li>
                  );
                })}
              </ul>
            </div>
          )}
        </div>

        <aside className="rounded-2xl border border-gray-200 dark:border-gray-800 bg-white dark:bg-white/[0.03] p-6 lg:sticky lg:top-8">
          <p className="text-xs uppercase tracking-wider text-gray-500 dark:text-gray-400 mb-1">
            Preço
          </p>
          {couponValidation ? (
            <div className="mb-4">
              <p className="text-sm text-gray-500 dark:text-gray-400 line-through tabular-nums">
                {formatBRL(product.price)}
              </p>
              <p className="text-3xl font-bold text-success-600 dark:text-success-400 tabular-nums">
                {formatBRL(totalAmount)}
              </p>
              <p className="text-[11px] text-success-700 dark:text-success-400 mt-1">
                Desconto de {formatBRL(couponValidation.discountAmount)} aplicado
              </p>
            </div>
          ) : (
            <p className="text-3xl font-bold text-gray-900 dark:text-white tabular-nums mb-4">
              {formatBRL(totalAmount)}
            </p>
          )}

          {/* Breakdown quando há bumps selecionados — comunica claramente o que
              está sendo cobrado e por quê. Sem bumps, esconde pra não poluir. */}
          {selectedBumps.length > 0 && (
            <div className="mb-4 rounded-lg bg-gray-50 dark:bg-gray-800/50 px-3 py-2 text-[11px] space-y-1">
              <div className="flex items-center justify-between">
                <span className="text-gray-600 dark:text-gray-400">{product.name}</span>
                <span className="tabular-nums text-gray-700 dark:text-gray-300">{formatBRL(mainPriceAfterCoupon)}</span>
              </div>
              {selectedBumps.map((b) => (
                <div key={b.id} className="flex items-center justify-between">
                  <span className="text-gray-600 dark:text-gray-400 truncate">+ {b.title}</span>
                  <span className="tabular-nums text-gray-700 dark:text-gray-300 shrink-0">
                    {formatBRL(b.finalPrice ?? b.price)}
                  </span>
                </div>
              ))}
            </div>
          )}

          {/* Cupom de desconto — input + botão "Aplicar". Backend valida ao
              submeter. Estado: empty → input + apply / applied → badge + remove. */}
          <div className="mb-4">
            {couponValidation ? (
              <div className="flex items-center justify-between rounded-lg bg-success-50 dark:bg-success-500/10 px-3 py-2 text-xs">
                <span className="text-success-700 dark:text-success-400">
                  Cupom <strong>{couponCode}</strong> aplicado
                </span>
                <button
                  type="button"
                  onClick={removeCoupon}
                  className="text-success-700 dark:text-success-400 hover:underline"
                >
                  Remover
                </button>
              </div>
            ) : (
              <details className="group">
                <summary className="text-xs text-gray-500 dark:text-gray-400 cursor-pointer hover:text-brand-600 dark:hover:text-brand-400 list-none flex items-center gap-1">
                  <svg width="11" height="11" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" className="transition-transform group-open:rotate-90"><polyline points="9 18 15 12 9 6" /></svg>
                  Tenho um cupom de desconto
                </summary>
                <div className="mt-2 flex items-center gap-2">
                  <input
                    value={couponCode}
                    onChange={(e) => { setCouponCode(e.target.value.toUpperCase()); setCouponError(null); }}
                    placeholder="CÓDIGO"
                    maxLength={32}
                    className="h-9 flex-1 rounded-lg border border-gray-200 dark:border-gray-700 bg-white dark:bg-gray-900 px-3 text-xs uppercase tabular-nums"
                  />
                  <button
                    type="button"
                    onClick={applyCoupon}
                    disabled={!couponCode.trim()}
                    className="h-9 rounded-lg bg-brand-500 hover:bg-brand-600 px-3 text-xs font-medium text-white disabled:opacity-50"
                  >
                    Aplicar
                  </button>
                </div>
                {couponError && (
                  <p className="mt-1 text-[11px] text-error-600 dark:text-error-400">{couponError}</p>
                )}
              </details>
            )}
          </div>

          {product.affiliate && (
            <div className="mb-4 rounded-lg bg-brand-50 dark:bg-brand-500/10 px-3 py-2 text-xs text-brand-700 dark:text-brand-400">
              Indicado por <strong>{product.affiliate.affiliateName ?? "afiliado"}</strong>
            </div>
          )}

          {/* Método picker — tabs visuais. 3 colunas espelham padrão Kirvano/Hotmart. */}
          <div className="grid grid-cols-3 gap-1 mb-4 p-1 bg-gray-100 dark:bg-gray-800 rounded-lg">
            {(["PIX", "CARTAO", "BOLETO"] as const).map((m) => (
              <button
                key={m}
                type="button"
                onClick={() => { setPaymentMethod(m); setCheckoutError(null); }}
                className={`h-9 rounded-md text-xs font-medium transition-colors ${
                  paymentMethod === m
                    ? "bg-white dark:bg-gray-900 text-gray-900 dark:text-white shadow-sm"
                    : "text-gray-600 dark:text-gray-400 hover:text-gray-900 dark:hover:text-white"
                }`}
              >
                {m === "PIX" ? "PIX" : m === "CARTAO" ? "Cartão" : "Boleto"}
              </button>
            ))}
          </div>

          {/* Form de payer comum aos 3 métodos. Cartão usa onSubmit próprio
              (via Elements). PIX/Boleto usam o handler do parent. */}
          {paymentMethod === "CARTAO" ? (
            <CardCheckoutPanel
              stripePromise={stripePromise}
              productPrice={totalAmount}
              buildBody={() => buildCheckoutBody("CARTAO")}
              onError={setCheckoutError}
              onSuccess={(result) => setCheckoutResult(result)}
              checkoutSlug={slug}
              externalError={checkoutError}
              payerName={payerName}
              setPayerName={setPayerName}
              payerEmail={payerEmail}
              setPayerEmail={setPayerEmail}
              payerDocument={payerDocument}
              setPayerDocument={setPayerDocument}
            />
          ) : (
            <form onSubmit={handleNonCardSubmit} className="space-y-3">
              <Field label="Seu nome" htmlFor="name">
                <input id="name" required value={payerName} onChange={(e) => setPayerName(e.target.value)} maxLength={120} className="h-10 w-full rounded-lg border border-gray-200 dark:border-gray-700 bg-white dark:bg-gray-900 px-3 text-sm" />
              </Field>
              <Field label="E-mail" htmlFor="email">
                <input id="email" type="email" required value={payerEmail} onChange={(e) => setPayerEmail(e.target.value)} maxLength={120} className="h-10 w-full rounded-lg border border-gray-200 dark:border-gray-700 bg-white dark:bg-gray-900 px-3 text-sm" />
              </Field>
              <Field label="CPF" htmlFor="doc">
                <input id="doc" inputMode="numeric" required value={payerDocument} onChange={(e) => setPayerDocument(e.target.value)} maxLength={14} placeholder="000.000.000-00" className="h-10 w-full rounded-lg border border-gray-200 dark:border-gray-700 bg-white dark:bg-gray-900 px-3 text-sm tabular-nums" />
              </Field>

              {checkoutError && (
                <div className="rounded-lg bg-error-50 dark:bg-error-500/10 px-3 py-2 text-xs text-error-700 dark:text-error-400">
                  {checkoutError}
                </div>
              )}

              <button
                type="submit"
                disabled={submitting}
                className="h-12 w-full rounded-lg bg-brand-500 hover:bg-brand-600 px-4 text-sm font-semibold text-white disabled:opacity-50"
              >
                {submitting
                  ? paymentMethod === "PIX" ? "Gerando PIX..." : "Gerando boleto..."
                  : paymentMethod === "PIX"
                    ? `Pagar com PIX · ${formatBRL(totalAmount)}`
                    : `Gerar boleto · ${formatBRL(totalAmount)}`}
              </button>
              <p className="text-[11px] text-gray-500 dark:text-gray-400 text-center mt-2">
                {paymentMethod === "PIX"
                  ? "Aprovação instantânea após o pagamento."
                  : "Boleto vence em 3 dias úteis. Compensação leva 1-2 dias após pagamento."}
              </p>
            </form>
          )}
        </aside>
      </div>
    </Shell>
  );
}

function Shell({ children }: { children: React.ReactNode }) {
  return (
    <div className="min-h-screen bg-gray-50 dark:bg-gray-950">
      <header className="border-b border-gray-200 dark:border-gray-800 bg-white dark:bg-gray-900">
        <div className="max-w-5xl mx-auto px-4 py-4 flex items-center justify-between">
          <span className="text-sm font-semibold text-gray-900 dark:text-white">
            Fellow <span className="text-brand-500">Pay</span>
          </span>
          <span className="text-[11px] text-gray-500 dark:text-gray-400">
            Pagamento seguro
          </span>
        </div>
      </header>
      <main className="max-w-5xl mx-auto px-4 py-8">{children}</main>
    </div>
  );
}

function Field({
  label,
  htmlFor,
  children,
}: {
  label: string;
  htmlFor: string;
  children: React.ReactNode;
}) {
  return (
    <div>
      <label htmlFor={htmlFor} className="block text-[11px] font-medium text-gray-700 dark:text-gray-300 mb-1">
        {label}
      </label>
      {children}
    </div>
  );
}

function CheckoutResultPanel({
  result,
  productName,
  pollStatus,
}: {
  result: PublicCheckoutResponse;
  productName: string;
  pollStatus: string | null;
}) {
  const payment = result.payment;
  const isCaptured = pollStatus === "CAPTURED";
  const isFailed = pollStatus === "FAILED" || pollStatus === "DECLINED" || pollStatus === "VOIDED";
  const pixCopy = payment.pixQrCode;
  const pixImg = payment.pixImageUrl;
  const boleto = payment.boletoUrl;
  const isPix = !!(pixCopy || pixImg);

  // Estado de sucesso unificado pros 3 métodos. PIX/Boleto chegam aqui assim
  // que o webhook do banco confirma; Cartão chega após Stripe.confirmPayment +
  // captura via webhook. Em todos os casos, a mensagem é a mesma: aguarde o
  // email com o link de acesso.
  if (isCaptured) {
    return (
      <div className="max-w-xl mx-auto rounded-2xl border border-success-200 dark:border-success-500/30 bg-white dark:bg-white/[0.03] p-8 text-center">
        <Illustration
          name="payment-success"
          size="lg"
          className="mx-auto mb-4"
        />
        <h2 className="text-xl font-semibold text-gray-900 dark:text-white">
          Pagamento confirmado!
        </h2>
        <p className="text-sm text-gray-500 dark:text-gray-400 mt-2">
          Você vai receber o acesso a <strong>{productName}</strong> no email cadastrado em alguns segundos.
        </p>
      </div>
    );
  }

  if (isFailed) {
    return (
      <div className="max-w-xl mx-auto rounded-2xl border border-error-200 dark:border-error-500/30 bg-white dark:bg-white/[0.03] p-8 text-center">
        <h2 className="text-lg font-semibold text-error-700 dark:text-error-400">Pagamento não confirmado</h2>
        <p className="text-sm text-gray-500 dark:text-gray-400 mt-2">
          O pagamento foi recusado ou cancelado. Tente novamente ou escolha outro método.
        </p>
        <button onClick={() => window.location.reload()} className="mt-4 h-10 px-4 rounded-lg bg-brand-500 hover:bg-brand-600 text-white text-sm font-medium">
          Tentar de novo
        </button>
      </div>
    );
  }

  return (
    <div className="max-w-xl mx-auto rounded-2xl border border-gray-200 dark:border-gray-800 bg-white dark:bg-white/[0.03] p-8">
      <h2 className="text-lg font-semibold text-gray-900 dark:text-white">
        Finalize seu pagamento
      </h2>
      <p className="text-sm text-gray-500 dark:text-gray-400 mt-1">
        Produto: <span className="font-medium">{productName}</span>
      </p>

      {isPix && (
        <div className="mt-6 space-y-4">
          {pixImg && (
            <div className="flex justify-center">
              {/* eslint-disable-next-line @next/next/no-img-element */}
              <img
                src={pixImg}
                alt="QR Code PIX"
                className="w-56 h-56 rounded-xl border border-gray-200 dark:border-gray-700 bg-white p-2"
              />
            </div>
          )}
          {pixCopy && (
            <div>
              <p className="text-[11px] uppercase tracking-wider text-gray-500 dark:text-gray-400 mb-1">
                Código copia e cola
              </p>
              <div className="flex items-center gap-2">
                <code className="flex-1 rounded-lg bg-gray-100 dark:bg-gray-800 px-3 py-2 text-[11px] font-mono break-all">
                  {pixCopy}
                </code>
                <button
                  onClick={() => navigator.clipboard.writeText(pixCopy)}
                  className="h-9 rounded-lg bg-brand-500 hover:bg-brand-600 px-3 text-xs font-medium text-white"
                >
                  Copiar
                </button>
              </div>
            </div>
          )}
          <p className="text-xs text-gray-500 dark:text-gray-400 text-center mt-4">
            Aguardando confirmação do banco... Após o PIX cair, você recebe o acesso por email.
          </p>
        </div>
      )}

      {boleto && (
        <div className="mt-6 space-y-4">
          <p className="text-sm text-gray-700 dark:text-gray-300">
            Seu boleto está pronto. Pague em qualquer banco/app — compensação leva 1-2 dias úteis.
          </p>
          <a
            href={boleto}
            target="_blank"
            rel="noopener noreferrer"
            className="block w-full text-center h-12 leading-[3rem] rounded-lg bg-brand-500 hover:bg-brand-600 text-white text-sm font-semibold"
          >
            Visualizar e baixar boleto
          </a>
          <p className="text-xs text-gray-500 dark:text-gray-400 text-center">
            Vencimento em 3 dias. Você receberá o acesso por email após a compensação.
          </p>
        </div>
      )}

      {!isPix && !boleto && (
        <div className="mt-6 space-y-3">
          <div className="flex items-center justify-center gap-2 text-sm text-gray-700 dark:text-gray-300">
            <div className="w-4 h-4 border-2 border-brand-500 border-t-transparent rounded-full animate-spin" />
            Aguardando confirmação do pagamento...
          </div>
          <p className="text-[11px] text-gray-500 dark:text-gray-400 text-center">
            Você receberá o acesso por email assim que confirmarmos.
          </p>
        </div>
      )}
    </div>
  );
}

/**
 * Subpanel pra fluxo de cartão — encapsula Stripe Elements no contexto.
 * Diferente do PIX/Boleto, cartão tem 2 passos:
 *   1. Cliente preenche dados → Stripe Elements valida client-side
 *   2. Submit: backend cria Transaction (que cria PaymentIntent no Stripe e
 *      retorna clientSecret) → frontend chama stripe.confirmPayment com esse
 *      clientSecret → Stripe processa, dispara 3DS se necessário, e webhook
 *      do Stripe atualiza o status da TX no backend.
 *
 * Importante: `redirect: "if_required"` faz Stripe NÃO redirecionar se não
 * precisar de 3DS — fica tudo na mesma página. Se precisar de 3DS, redireciona
 * pra `return_url`, que volta pra essa mesma página onde o polling pega o status.
 */
function CardCheckoutPanel(props: {
  stripePromise: Promise<Stripe | null> | null;
  productPrice: number;
  buildBody: () => PublicCheckoutRequest;
  onError: (msg: string) => void;
  onSuccess: (result: PublicCheckoutResponse) => void;
  checkoutSlug: string;
  externalError: string | null;
  payerName: string;
  setPayerName: (v: string) => void;
  payerEmail: string;
  setPayerEmail: (v: string) => void;
  payerDocument: string;
  setPayerDocument: (v: string) => void;
}) {
  const elementsOptions = useMemo(
    () => ({
      // Deferred Intent mode: render Elements com amount/currency ANTES de
      // ter clientSecret. Stripe cria o PaymentIntent só no confirmPayment.
      // Vantagem: páginas que abrem e fecham o checkout não consomem
      // PaymentIntents desnecessários (cada um custa $0.01 Stripe se cancelar).
      mode: "payment" as const,
      amount: Math.round(props.productPrice * 100),
      currency: "brl",
      appearance: { theme: "stripe" as const },
    }),
    [props.productPrice],
  );

  if (!props.stripePromise) {
    return (
      <div className="rounded-lg bg-gray-50 dark:bg-gray-800 p-4 text-xs text-gray-500 dark:text-gray-400">
        Carregando integração de cartão...
      </div>
    );
  }

  return (
    <Elements stripe={props.stripePromise} options={elementsOptions}>
      <CardCheckoutForm {...props} />
    </Elements>
  );
}

function CardCheckoutForm(props: {
  buildBody: () => PublicCheckoutRequest;
  onError: (msg: string) => void;
  onSuccess: (result: PublicCheckoutResponse) => void;
  checkoutSlug: string;
  externalError: string | null;
  payerName: string;
  setPayerName: (v: string) => void;
  payerEmail: string;
  setPayerEmail: (v: string) => void;
  payerDocument: string;
  setPayerDocument: (v: string) => void;
}) {
  const stripe = useStripe();
  const elements = useElements();
  const [submitting, setSubmitting] = useState(false);

  async function onSubmit(e: React.FormEvent) {
    e.preventDefault();
    if (!stripe || !elements) {
      props.onError("Stripe não carregou — recarregue a página.");
      return;
    }
    props.onError("");
    setSubmitting(true);
    try {
      // Step 1: Stripe valida o form client-side antes de chamar o backend.
      // Sem isso, criamos uma TX no backend desnecessariamente se o form tá inválido.
      const { error: submitError } = await elements.submit();
      if (submitError) {
        props.onError(submitError.message ?? "Verifique os dados do cartão.");
        return;
      }

      // Step 2: backend cria Transaction + PaymentIntent → retorna clientSecret.
      const result = await marketplaceService.checkoutProduct(
        props.checkoutSlug,
        props.buildBody(),
      );
      const clientSecret = result.payment.clientSecret;
      if (!clientSecret) {
        props.onError("Backend não retornou clientSecret — pagamento indisponível.");
        return;
      }

      // Step 3: confirma o pagamento com Stripe Elements. Se for 3DS, Stripe
      // pode redirecionar pro banco e voltar via return_url. Senão, fica inline.
      const { error: confirmError } = await stripe.confirmPayment({
        elements,
        clientSecret,
        confirmParams: {
          return_url: `${window.location.origin}/p/${props.checkoutSlug}`,
        },
        redirect: "if_required",
      });

      if (confirmError) {
        // Erros tipo "card_declined", "insufficient_funds" caem aqui.
        // Mensagens do Stripe são em PT-BR quando a locale do Elements é pt.
        props.onError(confirmError.message ?? "O pagamento foi recusado.");
        return;
      }

      // Sem erro = PaymentIntent foi pra "processing" ou "succeeded". Acionamos
      // o polling no parent passando o result; backend confirma via webhook.
      props.onSuccess(result);
    } catch (err) {
      props.onError(err instanceof Error ? err.message : "Erro inesperado ao processar pagamento.");
    } finally {
      setSubmitting(false);
    }
  }

  return (
    <form onSubmit={onSubmit} className="space-y-3">
      <Field label="Seu nome" htmlFor="name-card">
        <input id="name-card" required value={props.payerName} onChange={(e) => props.setPayerName(e.target.value)} maxLength={120} className="h-10 w-full rounded-lg border border-gray-200 dark:border-gray-700 bg-white dark:bg-gray-900 px-3 text-sm" />
      </Field>
      <Field label="E-mail" htmlFor="email-card">
        <input id="email-card" type="email" required value={props.payerEmail} onChange={(e) => props.setPayerEmail(e.target.value)} maxLength={120} className="h-10 w-full rounded-lg border border-gray-200 dark:border-gray-700 bg-white dark:bg-gray-900 px-3 text-sm" />
      </Field>
      <Field label="CPF" htmlFor="doc-card">
        <input id="doc-card" inputMode="numeric" required value={props.payerDocument} onChange={(e) => props.setPayerDocument(e.target.value)} maxLength={14} placeholder="000.000.000-00" className="h-10 w-full rounded-lg border border-gray-200 dark:border-gray-700 bg-white dark:bg-gray-900 px-3 text-sm tabular-nums" />
      </Field>

      <div className="pt-2">
        {/* PaymentElement renderiza form de cartão + carteiras (Apple Pay,
            Google Pay) com base no elementsOptions. Estilizado via appearance
            no Provider — segue stripe theme default por enquanto. */}
        <PaymentElement options={{ layout: "tabs" }} />
      </div>

      {props.externalError && (
        <div className="rounded-lg bg-error-50 dark:bg-error-500/10 px-3 py-2 text-xs text-error-700 dark:text-error-400">
          {props.externalError}
        </div>
      )}

      <button
        type="submit"
        disabled={submitting || !stripe || !elements}
        className="h-12 w-full rounded-lg bg-brand-500 hover:bg-brand-600 px-4 text-sm font-semibold text-white disabled:opacity-50"
      >
        {submitting ? "Processando pagamento..." : "Pagar com cartão"}
      </button>
      <p className="text-[11px] text-gray-500 dark:text-gray-400 text-center mt-2">
        Aprovação imediata. Cartão processado com segurança pelo Stripe.
      </p>
    </form>
  );
}
