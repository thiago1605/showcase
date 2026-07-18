"use client";
import React, { useEffect, useMemo, useRef, useState } from "react";
import { useParams } from "next/navigation";
import { loadStripe, type Stripe } from "@stripe/stripe-js";
import {
  CardElement,
  Elements,
  ExpressCheckoutElement,
  useElements,
  useStripe,
} from "@stripe/react-stripe-js";
import { paymentLinksService, type PayResult } from "@/services/payment-links.service";
import { paymentTypeLabel } from "@/lib/formatters/enums";
import styles from "./checkout.module.css";

interface ResolvedLink {
  amount: number;
  paymentType: string;
  /** Lista de métodos aceitos. Sempre ≥1. Quando >1, exibimos seletor. */
  paymentTypes: string[];
  installments: number;
  description: string | null;
  sellerName: string | null;
}

type Stage =
  | "loading"
  | "invalid"
  | "method_choice" // novo: link multi-método, customer escolhe antes do flow
  | "card_init"
  | "card_ready"
  | "brl_form"
  | "brl_submitting"
  | "brl_awaiting"
  | "success"
  | "error";

const METHOD_DISPLAY: Record<string, { label: string; description: string; icon: React.ReactNode }> = {
  PIX: {
    label: "Pix",
    description: "QR Code ou copia-e-cola. Confirmação imediata.",
    icon: (
      <svg width="20" height="20" viewBox="0 0 24 24" fill="none" aria-hidden="true">
        <path d="M5 12l7 7 7-7-7-7-7 7z" stroke="currentColor" strokeWidth="1.5" strokeLinejoin="round" />
      </svg>
    ),
  },
  CREDIT_CARD: {
    label: "Cartão de crédito",
    description: "Apple Pay, Google Pay, Link ou cartão manual.",
    icon: (
      <svg width="20" height="20" viewBox="0 0 24 24" fill="none" aria-hidden="true">
        <rect x="3" y="6" width="18" height="13" rx="2" stroke="currentColor" strokeWidth="1.5" />
        <path d="M3 10h18" stroke="currentColor" strokeWidth="1.5" />
      </svg>
    ),
  },
  DEBIT_CARD: {
    label: "Cartão de débito",
    description: "Pagamento à vista no débito.",
    icon: (
      <svg width="20" height="20" viewBox="0 0 24 24" fill="none" aria-hidden="true">
        <rect x="3" y="6" width="18" height="13" rx="2" stroke="currentColor" strokeWidth="1.5" />
        <path d="M3 10h18M7 15h4" stroke="currentColor" strokeWidth="1.5" strokeLinecap="round" />
      </svg>
    ),
  },
  BOLETO: {
    label: "Boleto",
    description: "Compensação em até 2 dias úteis.",
    icon: (
      <svg width="20" height="20" viewBox="0 0 24 24" fill="none" aria-hidden="true">
        <path d="M5 4v16M9 4v16M13 4v16M17 4v16M21 4v16" stroke="currentColor" strokeWidth="1.5" strokeLinecap="round" />
      </svg>
    ),
  },
};

const formatCurrency = (value: number) =>
  new Intl.NumberFormat("pt-BR", { style: "currency", currency: "BRL" }).format(value);

function maskDocument(raw: string): string {
  const d = raw.replace(/\D/g, "").slice(0, 14);
  if (d.length <= 11) {
    return d
      .replace(/^(\d{3})(\d)/, "$1.$2")
      .replace(/^(\d{3})\.(\d{3})(\d)/, "$1.$2.$3")
      .replace(/\.(\d{3})(\d)/, ".$1-$2");
  }
  return d
    .replace(/^(\d{2})(\d)/, "$1.$2")
    .replace(/^(\d{2})\.(\d{3})(\d)/, "$1.$2.$3")
    .replace(/\.(\d{3})(\d)/, ".$1/$2")
    .replace(/(\d{4})(\d)/, "$1-$2");
}

// Light/financeiro theme — combina com a paleta clara da página de checkout. Stripe
// Elements seguem o mesmo idioma visual do resto da UI (Pagar.me / Iugu / Asaas-like)
// em vez do "stripe escuro" padrão.
const STRIPE_APPEARANCE = {
  theme: "stripe" as const,
  variables: {
    colorPrimary: "#6D4CFF",
    colorBackground: "#FFFFFF",
    colorText: "#111827",
    colorTextSecondary: "#6B7280",
    colorTextPlaceholder: "#9CA3AF",
    colorDanger: "#DC2626",
    fontFamily: "-apple-system, BlinkMacSystemFont, 'Inter', 'Segoe UI', Roboto, sans-serif",
    borderRadius: "8px",
    spacingUnit: "4px",
  },
  rules: {
    ".Input": { borderColor: "#D4D4D8", backgroundColor: "#FFFFFF" },
    ".Input:focus": { borderColor: "#6D4CFF", boxShadow: "0 0 0 3px rgba(109, 76, 255, 0.12)" },
    ".Label": { color: "#111827", fontWeight: "500" },
    ".Tab": { borderColor: "#E5E7EB" },
    ".Tab--selected": { borderColor: "#6D4CFF" },
  },
};

// Wordmarks curtos por bandeira — sem bitmaps (evita questão de licenciamento).
// Usados em chips inline pra "Aceitamos: VISA · MASTERCARD · ELO …" no checkout.
const BRAND_LABELS: Record<string, string> = {
  visa: "VISA",
  mastercard: "MASTERCARD",
  amex: "AMEX",
  diners: "DINERS",
  discover: "DISCOVER",
  jcb: "JCB",
  unionpay: "UNIONPAY",
  elo: "ELO",
  hipercard: "HIPERCARD",
};

// Lista exibida no checkout. Mais curta que a base — só as 5 bandeiras BR comuns.
const ACCEPTED_BRANDS = ["visa", "mastercard", "elo", "amex", "hipercard"] as const;

/**
 * Chips de bandeiras aceitas. Destaca a bandeira detectada pelo Stripe (via
 * `onChange.brand` do CardElement) com background brand-tint. Sem requisição
 * externa de imagem, sem dependência de SVG de marca registrada.
 */
function AcceptedBrandChips({ detected }: { detected: string | null }) {
  return (
    <div className={styles.brandChips} aria-label="Bandeiras aceitas">
      {ACCEPTED_BRANDS.map((b) => {
        const active = detected === b;
        return (
          <span
            key={b}
            className={`${styles.brandChip} ${active ? styles.brandChipActive : ""}`}
            aria-current={active ? "true" : undefined}
          >
            {BRAND_LABELS[b]}
          </span>
        );
      })}
    </div>
  );
}

// Brazilian-market checkout pattern (matches Pagar.me / Mercado Pago / wwwroot/checkout.html):
// Express Checkout (Apple Pay / Google Pay / Link) on top, separator, single-line CardElement
// below. Uses Stripe deferred-intent mode: the PaymentIntent is created on the backend ONLY
// when the customer confirms (wallet click or card submit). This is also when the link's
// usage slot is reserved — page reloads no longer burn UsageCount.
function CardCheckout({
  token,
  amount,
  chosenMethod,
  onPaid,
  onError,
}: {
  token: string;
  amount: number;
  /** "CREDIT_CARD" ou "DEBIT_CARD" — informado ao backend pra link multi-método. */
  chosenMethod: string;
  onPaid: (result: PayResult) => void;
  onError: (msg: string) => void;
}) {
  const stripe = useStripe();
  const elements = useElements();
  const [walletsAvailable, setWalletsAvailable] = useState<string[] | null>(null);
  const [submitting, setSubmitting] = useState(false);
  const [cardError, setCardError] = useState<string | null>(null);
  const [email, setEmail] = useState("");
  const [holderName, setHolderName] = useState("");
  const [payerDocument, setPayerDocument] = useState("");
  // Brand detectada pelo Stripe (CardElement.onChange) — destacada na lista
  // "aceitamos" abaixo, ajuda o cliente a confirmar visualmente que o cartão dele
  // foi reconhecido. Não recebemos número/CVC, apenas a string da bandeira.
  const [cardBrand, setCardBrand] = useState<string | null>(null);
  // Parcelamento sem juros (modo onde seller absorve o adicional Stripe). Carrega
  // do endpoint `/installments`; dropdown só aparece quando há ≥ 2 opções. Conta
  // Stripe BR que ainda não tem installments habilitado retorna [] ou [1x] — UI
  // some sozinha, sem necessidade de feature-flag manual.
  const [installmentOptions, setInstallmentOptions] = useState<
    Array<{ count: number; perInstallmentAmount: number; total: number }>
  >([]);
  const [chosenInstallments, setChosenInstallments] = useState<number>(1);

  const intentRef = useRef<PayResult | null>(null);

  // Carrega opções de parcelamento ao montar. Endpoint público anônimo. Falha
  // silenciosa (manter chosenInstallments = 1) — installments é feature opt-in,
  // não pode bloquear o checkout.
  useEffect(() => {
    let cancelled = false;
    paymentLinksService
      .installmentOptions(token)
      .then((opts) => {
        if (cancelled) return;
        setInstallmentOptions(opts);
      })
      .catch(() => {
        if (cancelled) return;
        setInstallmentOptions([]);
      });
    return () => {
      cancelled = true;
    };
  }, [token]);

  // Asks the backend to create the PaymentIntent NOW (this is the call that reserves
  // the link usage). We cache the result so the success screen can show internalId.
  const createIntent = async (payer: {
    payerName?: string;
    payerDocument?: string;
    payerEmail?: string;
  }): Promise<PayResult | null> => {
    try {
      const result = await paymentLinksService.pay(token, {
        ...payer,
        chosenPaymentType: chosenMethod,
        // Só envia parcelas quando o cliente escolheu > 1 (evita pollute em fluxos
        // débito/PIX onde o campo é ignorado pelo backend de qualquer forma).
        chosenInstallments: chosenInstallments > 1 ? chosenInstallments : undefined,
      });
      intentRef.current = result;
      return result;
    } catch (err) {
      onError(err instanceof Error ? err.message : "Não foi possível iniciar o pagamento.");
      return null;
    }
  };

  const handleCardSubmit = async (e: React.FormEvent) => {
    e.preventDefault();
    if (!stripe || !elements) return;
    const card = elements.getElement(CardElement);
    if (!card) return;

    // Basic validation: CPF (11) or CNPJ (14) digits.
    const docDigits = payerDocument.replace(/\D/g, "");
    if (docDigits.length !== 11 && docDigits.length !== 14) {
      onError("Informe um CPF (11 dígitos) ou CNPJ (14 dígitos) válido.");
      return;
    }

    setSubmitting(true);
    setCardError(null);

    const intent = await createIntent({
      payerName: holderName.trim() || undefined,
      payerEmail: email.trim() || undefined,
      payerDocument: docDigits,
    });
    if (!intent || !intent.payment.clientSecret) {
      setSubmitting(false);
      return;
    }

    const { error, paymentIntent } = await stripe.confirmCardPayment(intent.payment.clientSecret, {
      payment_method: {
        card,
        billing_details: {
          name: holderName.trim() || undefined,
          email: email.trim() || undefined,
        },
      },
    });

    if (error) {
      const msg = error.message || "Verifique os dados do cartão.";
      setCardError(msg);
      onError(msg);
      setSubmitting(false);
      return;
    }
    if (paymentIntent && (paymentIntent.status === "succeeded" || paymentIntent.status === "processing")) {
      onPaid(intent);
      return;
    }
    setSubmitting(false);
    onError("Pagamento não foi concluído. Tente novamente.");
  };

  return (
    <div>
      <ExpressCheckoutElement
        options={{
          buttonType: { applePay: "buy", googlePay: "buy" },
          buttonTheme: { applePay: "white-outline", googlePay: "white" },
          layout: { maxColumns: 2, maxRows: 1, overflow: "auto" },
          // Stripe Link desabilitado: o pill flutuante "stripe →" no canto inferior
          // direito + o branding "Link" dentro da row de carteiras competiam com
          // a marca whitelabel do tenant. Apple Pay e Google Pay ficam.
          // Reativar quando houver UX dedicado pra explicar Link pro comprador BR.
          paymentMethods: { link: "never" },
        }}
        onReady={(event) => {
          const av = (event.availablePaymentMethods ?? {}) as Record<string, boolean | undefined>;
          const list: string[] = [];
          if (av.applePay) list.push("Apple Pay");
          if (av.googlePay) list.push("Google Pay");
          if (av.link) list.push("Link");
          setWalletsAvailable(list);
        }}
        onConfirm={async (event) => {
          if (!stripe || !elements) return;
          // Stripe requires elements.submit() before creating the PaymentIntent in
          // deferred mode — it validates the wallet's data.
          const { error: submitError } = await elements.submit();
          if (submitError) {
            onError(submitError.message || "Não foi possível confirmar o pagamento.");
            return;
          }
          // Wallets fornecem nome/email — passamos pro backend pra evitar placeholder.
          // `billingDetails` is loosely typed by @stripe/stripe-js depending on the
          // collected fields config; cast to read the wallet-provided values safely.
          const billing = (event.billingDetails ?? {}) as {
            name?: string;
            email?: string;
          };
          const intent = await createIntent({
            payerName: billing.name || undefined,
            payerEmail: billing.email || undefined,
          });
          if (!intent || !intent.payment.clientSecret) return;

          const { error, paymentIntent } = await stripe.confirmPayment({
            elements,
            clientSecret: intent.payment.clientSecret,
            confirmParams: { return_url: window.location.href },
            redirect: "if_required",
          });
          if (error) {
            onError(error.message || "Não foi possível confirmar o pagamento.");
            return;
          }
          if (paymentIntent && (paymentIntent.status === "succeeded" || paymentIntent.status === "processing")) {
            onPaid(intent);
          } else {
            onError("Pagamento não foi concluído.");
          }
        }}
      />

      {walletsAvailable !== null && walletsAvailable.length === 0 && (
        <p style={{ fontSize: 12, color: "#6b7280", textAlign: "center", margin: "8px 0 0" }}>
          Nenhuma carteira digital disponível neste dispositivo.
        </p>
      )}

      <div className={styles.separator}>
        <span>ou pague com cartão</span>
      </div>

      {/* Cartão visual decorativo removido (2026-05-15): ocupava ~200px sem informar
          nada além de placeholders. Modern checkout pattern (Stripe Checkout, Mercado
          Pago) é form direto, especialmente importante em mobile onde o espaço é caro. */}

      <form onSubmit={handleCardSubmit}>
        <label className={styles.label}>Email <span style={{ fontSize: 11, color: "#9ca3af", fontWeight: 400 }}>(para o recibo)</span></label>
        <input
          type="email"
          className={styles.input}
          value={email}
          onChange={(e) => setEmail(e.target.value)}
          placeholder="voce@email.com"
          required
          maxLength={256}
        />

        <label className={styles.label}>Nome do titular</label>
        <input
          type="text"
          className={styles.input}
          value={holderName}
          onChange={(e) => setHolderName(e.target.value)}
          placeholder="Como está no cartão"
          required
          maxLength={200}
        />

        <label className={styles.label}>CPF/CNPJ do pagador</label>
        <input
          type="text"
          inputMode="numeric"
          className={styles.input}
          value={payerDocument}
          onChange={(e) => setPayerDocument(maskDocument(e.target.value))}
          placeholder="000.000.000-00"
          required
          maxLength={18}
        />
        <p style={{ fontSize: 11, color: "#9ca3af", marginTop: -10, marginBottom: 14, lineHeight: 1.5 }}>
          Usado para segurança da transação e emissão de comprovante.
        </p>

        <label className={styles.label}>Cartão</label>
        <div className={styles.cardElementWrap}>
          <CardElement
            options={{
              hidePostalCode: true,
              style: {
                base: {
                  fontSize: "14px",
                  color: "#111827",
                  iconColor: "#6B7280",
                  fontFamily: "-apple-system, BlinkMacSystemFont, 'Inter', 'Segoe UI', Roboto, sans-serif",
                  "::placeholder": { color: "#9CA3AF" },
                },
                invalid: { color: "#DC2626", iconColor: "#DC2626" },
              },
            }}
            onChange={(e) => {
              setCardError(e.error ? e.error.message : null);
              // Stripe expõe `brand` no change event ("visa"|"mastercard"|... ou "unknown").
              // Não recebemos número, validade ou CVC — apenas a bandeira detectada.
              setCardBrand(e.brand && e.brand !== "unknown" ? e.brand : null);
            }}
          />
        </div>
        <AcceptedBrandChips detected={cardBrand} />
        {cardError && (
          <p role="alert" style={{ fontSize: 12, color: "#DC2626", marginTop: -8, marginBottom: 12 }}>{cardError}</p>
        )}

        {/* Seletor de parcelas — só aparece quando o backend retorna ≥2 opções.
            Conta Stripe BR sem installments habilitado retorna apenas [1x] e o
            componente fica invisível. Modo "sem juros": comprador paga o mesmo total
            independente do N (seller absorve o adicional Stripe via fee escalonado). */}
        {installmentOptions.length > 1 && (
          <div className={styles.installmentsWrap}>
            <label className={styles.label} htmlFor="installments-select">Parcelas</label>
            <select
              id="installments-select"
              className={styles.input}
              value={chosenInstallments}
              onChange={(e) => setChosenInstallments(parseInt(e.target.value, 10))}
              disabled={submitting}
              style={{ marginBottom: 8 }}
            >
              {installmentOptions.map((o) => (
                <option key={o.count} value={o.count}>
                  {o.count}x de {formatCurrency(o.perInstallmentAmount)}
                  {o.count > 1 ? " sem juros" : ""}
                </option>
              ))}
            </select>
            <p className={styles.installmentsHint}>
              Total: {formatCurrency(amount)} — sem acréscimo, independente do parcelamento.
            </p>
          </div>
        )}

        <button
          type="submit"
          className={styles.btnPrimary}
          disabled={!stripe || !elements || submitting}
          style={{ marginTop: 4 }}
        >
          {submitting
            ? "Verificando com seu banco…"
            : chosenInstallments > 1
              ? `Pagar ${chosenInstallments}x de ${formatCurrency(amount / chosenInstallments)}`
              : `Pagar ${formatCurrency(amount)}`}
        </button>
        {submitting && (
          <p className={styles.processingHint} role="status" aria-live="polite">
            Pode levar alguns segundos. Não feche nem atualize a página.
          </p>
        )}
        <TermsLine />
      </form>
    </div>
  );
}

function BrlPayerForm({
  onSubmit,
  submitting,
  errorMessage,
}: {
  onSubmit: (payer: { payerName: string; payerDocument: string; payerEmail: string; payerPhone?: string }) => void;
  submitting: boolean;
  errorMessage: string | null;
}) {
  const [payerName, setPayerName] = useState("");
  const [payerDocument, setPayerDocument] = useState("");
  const [payerEmail, setPayerEmail] = useState("");
  const [payerPhone, setPayerPhone] = useState("");

  const handle = (e: React.FormEvent) => {
    e.preventDefault();
    onSubmit({
      payerName: payerName.trim(),
      payerDocument: payerDocument.replace(/\D/g, ""),
      payerEmail: payerEmail.trim(),
      payerPhone: payerPhone ? payerPhone.replace(/\D/g, "") : undefined,
    });
  };

  return (
    <form onSubmit={handle}>
      {errorMessage && <div className={styles.alertError} role="alert">{errorMessage}</div>}

      <label className={styles.label}>Nome completo</label>
      <input
        type="text"
        className={styles.input}
        value={payerName}
        onChange={(e) => setPayerName(e.target.value)}
        required
        maxLength={200}
      />

      <div className={styles.row}>
        <div>
          <label className={styles.label}>CPF / CNPJ</label>
          <input
            type="text"
            inputMode="numeric"
            className={styles.input}
            value={payerDocument}
            onChange={(e) => setPayerDocument(maskDocument(e.target.value))}
            placeholder="000.000.000-00"
            required
          />
        </div>
        <div>
          <label className={styles.label}>
            Telefone <span style={{ fontSize: 11, color: "#9ca3af", fontWeight: 400 }}>(opcional)</span>
          </label>
          <input
            type="tel"
            className={styles.input}
            value={payerPhone}
            onChange={(e) => setPayerPhone(e.target.value)}
            placeholder="(11) 99999-9999"
          />
        </div>
      </div>

      <label className={styles.label}>Email</label>
      <input
        type="email"
        className={styles.input}
        value={payerEmail}
        onChange={(e) => setPayerEmail(e.target.value)}
        required
        maxLength={256}
      />

      <button type="submit" className={styles.btnPrimary} disabled={submitting} style={{ marginTop: 8 }}>
        {submitting ? "Gerando…" : "Continuar"}
      </button>
      <TermsLine />
    </form>
  );
}

// Discreet legal line shown below the primary action. Both URLs are configurable
// via NEXT_PUBLIC_TERMS_URL / NEXT_PUBLIC_PRIVACY_URL with sensible defaults.
const TERMS_URL = process.env.NEXT_PUBLIC_TERMS_URL || "https://fellowpay.com.br/termos";
const PRIVACY_URL = process.env.NEXT_PUBLIC_PRIVACY_URL || "https://fellowpay.com.br/privacidade";

function TermsLine() {
  return (
    <p className={styles.termsLine}>
      Ao continuar, você concorda com os{" "}
      <a href={TERMS_URL} target="_blank" rel="noopener noreferrer">Termos de Uso</a>
      {" "}e a{" "}
      <a href={PRIVACY_URL} target="_blank" rel="noopener noreferrer">Política de Privacidade</a>.
    </p>
  );
}

function AwaitingNotice({ text }: { text: string }) {
  return (
    <div className={styles.awaiting} role="status" aria-live="polite">
      <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true">
        <circle cx="12" cy="12" r="10" />
        <polyline points="12 6 12 12 16 14" />
      </svg>
      <span>{text}</span>
    </div>
  );
}

function PixInstrument({ qr, image }: { qr: string | null; image: string | null }) {
  const [copied, setCopied] = useState(false);
  const handleCopy = () => {
    if (!qr) return;
    navigator.clipboard.writeText(qr);
    setCopied(true);
    setTimeout(() => setCopied(false), 2000);
  };
  return (
    <div className={styles.pixWrap}>
      {image ? (
        // eslint-disable-next-line @next/next/no-img-element
        <img src={image} alt="QR Code Pix" className={styles.pixQrImage} />
      ) : qr ? null : (
        <p className={styles.alertError} style={{ marginBottom: 16 }}>
          Não foi possível gerar o QR Code Pix. Tente novamente.
        </p>
      )}
      {qr && (
        <>
          <div className={styles.pixCopyBox}>
            <p className={styles.pixCopyLabel}>Pix copia e cola</p>
            <p className={styles.pixCopyCode}>{qr}</p>
          </div>
          <button onClick={handleCopy} className={styles.btnPrimary}>
            {copied ? "Copiado!" : "Copiar código"}
          </button>
        </>
      )}
      <AwaitingNotice text="Aguardando pagamento — a confirmação acontece em segundos após você pagar no app do banco." />
    </div>
  );
}

function BoletoInstrument({ url }: { url: string | null }) {
  if (!url) {
    return <p className={styles.alertError}>Não foi possível gerar o boleto. Tente novamente.</p>;
  }
  return (
    <div>
      <a href={url} target="_blank" rel="noopener noreferrer" className={styles.btnSecondary}>
        Abrir boleto
      </a>
      <AwaitingNotice text="Aguardando pagamento — a compensação do boleto pode levar até 2 dias úteis." />
    </div>
  );
}

export default function PublicCheckoutPage() {
  const params = useParams<{ token: string }>();
  const token = params.token;

  const [stage, setStage] = useState<Stage>("loading");
  const [link, setLink] = useState<ResolvedLink | null>(null);
  const [errorMessage, setErrorMessage] = useState<string | null>(null);
  const [paymentResult, setPaymentResult] = useState<PayResult | null>(null);
  const [stripePromise, setStripePromise] = useState<Promise<Stripe | null> | null>(null);
  /** Em links multi-método (paymentTypes.length > 1), guarda o método escolhido pelo
   *  cliente. Em link single-method, espelha link.paymentType automaticamente. */
  const [chosenMethod, setChosenMethod] = useState<string | null>(null);

  const effectiveMethod = chosenMethod ?? link?.paymentType ?? null;
  const isCard = effectiveMethod === "CREDIT_CARD" || effectiveMethod === "DEBIT_CARD";
  const isPix = effectiveMethod === "PIX";
  const isBoleto = effectiveMethod === "BOLETO";

  // Step 1: resolve the link.
  useEffect(() => {
    let cancelled = false;
    (async () => {
      try {
        const raw = await paymentLinksService.resolve(token);
        if (cancelled) return;

        // Limite operacional Woovi/OpenPix (PIX IN): R$ 800/TX. Quando o link tem
        // amount maior, removemos PIX das opções do checkout. Espelha a guard
        // backend (PixLimits.IsInboundAmountAllowed) — UX falha-rápido em vez
        // de deixar o cliente clicar PIX e bater num 400 do servidor.
        const PIX_MAX_AMOUNT = 800;
        let data = raw;
        if (raw.amount > PIX_MAX_AMOUNT && raw.paymentTypes.includes("PIX")) {
          const filtered = raw.paymentTypes.filter((t) => t !== "PIX");
          if (filtered.length === 0) {
            // Link só aceita PIX e o valor não cabe — bloqueia com mensagem clara.
            setErrorMessage(
              `Pagamentos via PIX estão limitados a R$ ${PIX_MAX_AMOUNT.toFixed(2)} por transação. ` +
              `Este link está acima deste valor — peça ao vendedor um link com Cartão ou Boleto.`,
            );
            setLink(raw);
            setStage("invalid");
            return;
          }
          // Mantém os outros métodos. O `raw` original fica preservado pra exibição,
          // só o array de paymentTypes que o customer vê é filtrado.
          data = { ...raw, paymentTypes: filtered };
        }

        setLink(data);
        // Se o link aceita >1 método, o cliente escolhe primeiro. Se aceita 1 só
        // (ou é legacy single), pulamos direto pro flow do método.
        if (data.paymentTypes.length > 1) {
          setStage("method_choice");
          return;
        }
        const single = data.paymentTypes[0] ?? data.paymentType;
        setChosenMethod(single);
        const card = single === "CREDIT_CARD" || single === "DEBIT_CARD";
        setStage(card ? "card_init" : "brl_form");
      } catch (err) {
        if (cancelled) return;
        setErrorMessage(err instanceof Error ? err.message : "Não foi possível carregar este link de pagamento.");
        setStage("invalid");
      }
    })();
    return () => {
      cancelled = true;
    };
  }, [token]);

  // Step 2 (card only): load Stripe.js. We DON'T call /pay yet — that would reserve a
  // usage slot on the link before the customer actually pays (and would burn it on every
  // page reload). Instead we initialize Stripe Elements in *deferred intent* mode below
  // (mode/amount/currency only). The PaymentIntent is created on submit/wallet-confirm,
  // which is also when the link's UsageCount actually increments.
  useEffect(() => {
    if (stage !== "card_init" || !link) return;
    let cancelled = false;
    (async () => {
      try {
        const cfg = await paymentLinksService.getCheckoutConfig();
        if (!cfg.stripePk) {
          if (!cancelled) {
            setErrorMessage("Pagamento por cartão indisponível neste ambiente.");
            setStage("error");
          }
          return;
        }
        if (cancelled) return;
        // TODO: when sellers come online with Stripe Connect, we'll need the seller's
        // stripeAccountId before init. Either expose it via /payment-links/pay/{token}
        // resolve, or via a /checkout/config?seller=... lookup.
        setStripePromise(loadStripe(cfg.stripePk));
        setStage("card_ready");
      } catch (err) {
        if (cancelled) return;
        setErrorMessage(err instanceof Error ? err.message : "Não foi possível iniciar o pagamento.");
        setStage("error");
      }
    })();
    return () => {
      cancelled = true;
    };
  }, [stage, link]);

  // Deferred intent mode: Stripe Elements renders wallets + card form using only
  // amount/currency, no clientSecret needed. The PaymentIntent is created at confirm time.
  // P1-5: Pix polling. Once the Pix QR is rendered (brl_awaiting), poll the public
  // status endpoint every 4s until the transaction reaches a terminal state. Stops
  // automatically on success/timeout/unmount. Boleto is intentionally excluded —
  // compensação leva 1–2 dias e o cliente não fica na página esperando.
  useEffect(() => {
    if (stage !== "brl_awaiting" || !isPix) return;
    const txId = paymentResult?.internalId;
    if (!txId) return;

    let cancelled = false;
    let attempts = 0;
    const MAX_ATTEMPTS = 150; // ~10 min @ 4s — alinhado com expiração típica do QR Pix
    const INTERVAL_MS = 4000;

    const tick = async () => {
      if (cancelled || attempts >= MAX_ATTEMPTS) return;
      attempts++;
      try {
        const res = await paymentLinksService.getTransactionStatus(token, txId);
        if (cancelled) return;
        if (res.status === "CAPTURED") {
          setStage("success");
          return;
        }
        if (res.isTerminal) {
          // FAILED / DECLINED / VOIDED / etc — para de tentar e mostra erro amigável.
          setErrorMessage("O Pix não foi confirmado. Gere um novo QR ou tente outra forma.");
          setStage("error");
        }
      } catch {
        // Erro transitório de rede: tolera, próxima iteração tenta de novo.
      }
    };

    const handle = setInterval(tick, INTERVAL_MS);
    // Primeira chamada imediata pra reduzir latência percebida
    void tick();
    return () => {
      cancelled = true;
      clearInterval(handle);
    };
  }, [stage, isPix, paymentResult, token]);

  const elementsOptions = useMemo(() => {
    if (!link?.amount) return null;
    return {
      mode: "payment" as const,
      amount: Math.round(link.amount * 100), // Stripe expects smallest currency unit
      currency: "brl",
      paymentMethodCreation: "manual" as const,
      appearance: STRIPE_APPEARANCE,
    };
  }, [link]);

  const handleBrlSubmit = async (payer: {
    payerName: string;
    payerDocument: string;
    payerEmail: string;
    payerPhone?: string;
  }) => {
    setStage("brl_submitting");
    setErrorMessage(null);
    try {
      // chosenMethod sempre tem valor neste ponto: ou foi setado pelo método único
      // do link, ou foi escolhido pelo customer no method_choice step.
      const result = await paymentLinksService.pay(token, {
        ...payer,
        chosenPaymentType: chosenMethod ?? undefined,
      });
      setPaymentResult(result);
      setStage("brl_awaiting");
    } catch (err) {
      setErrorMessage(err instanceof Error ? err.message : "Não foi possível processar o pagamento.");
      setStage("brl_form");
    }
  };

  return (
    <div className={styles.shell}>
      <header className={styles.header}>
        <div className={styles.headerLeft}>
          {/* eslint-disable-next-line @next/next/no-img-element */}
          <img src="/images/fellow/fellow-pay-full-logo-no-bg-light-mode.png" alt="Fellow Pay" className={styles.headerLogo} />
        </div>
        <div className={styles.headerSecure}>
          <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true">
            <rect x="3" y="11" width="18" height="11" rx="2" ry="2" />
            <path d="M7 11V7a5 5 0 0 1 10 0v4" />
          </svg>
          <span>Pagamento seguro</span>
        </div>
      </header>

      <main className={styles.main}>
        {stage === "loading" && (
          <div className={styles.panel} role="status" aria-live="polite">
            <div className={styles.loadingCard}>
              <div className={styles.spinner} aria-hidden="true" />
              <p>Carregando link de pagamento…</p>
            </div>
          </div>
        )}

        {stage === "invalid" && (
          <div className={styles.panel} role="alert" aria-live="assertive">
            <div className={styles.successCard}>
              <h2>Link não disponível</h2>
              <p>{errorMessage ?? "Este link expirou, foi esgotado ou não existe."}</p>
            </div>
          </div>
        )}

        {stage === "error" && (
          <div className={styles.panel} role="alert" aria-live="assertive">
            <div className={styles.successCard}>
              <h2>Não foi possível continuar</h2>
              <p>{errorMessage ?? "Tente novamente em instantes."}</p>
              <button
                type="button"
                className={styles.btnPrimary}
                style={{ marginTop: 18, maxWidth: 220, marginInline: "auto" }}
                onClick={() => {
                  setErrorMessage(null);
                  // Re-runs the card_init / brl_form transition based on link.paymentType.
                  setStage(isCard ? "card_init" : "brl_form");
                }}
              >
                Tentar novamente
              </button>
            </div>
          </div>
        )}

        {stage === "success" && paymentResult && (
          <div aria-live="polite">
            <div className={styles.pageTitle}>
              <h1>Pagamento confirmado</h1>
            </div>
            <div className={styles.panel}>
              <div className={styles.successCard}>
                <div className={styles.successIcon} aria-hidden="true">
                  <svg width="28" height="28" viewBox="0 0 24 24" fill="none" stroke="#16a34a" strokeWidth="3" strokeLinecap="round" strokeLinejoin="round">
                    <path d="M5 13l4 4L19 7" />
                  </svg>
                </div>
                <h2>Tudo certo!</h2>
                <p>Anote o número da transação abaixo para sua referência.</p>
                <p className={styles.txId}>Transação: {paymentResult.internalId}</p>
              </div>
            </div>
          </div>
        )}

        {link && stage !== "loading" && stage !== "invalid" && stage !== "success" && stage !== "error" && (
          <>
            <div className={styles.pageTitle}>
              <h1>Finalizar pagamento</h1>
              <p>Confira o resumo e escolha a forma de pagamento.</p>
            </div>

            <div className={styles.layout}>
              <div className={styles.panel}>
                {link.sellerName && (
                  <p className={styles.sellerLine}>
                    Você está pagando <strong>{link.sellerName}</strong>
                  </p>
                )}
                <p className={styles.amountLabel}>Total a pagar</p>
                <p className={styles.amountBig}>{formatCurrency(link.amount)}</p>
                {link.description && <p className={styles.amountDescription}>{link.description}</p>}
                {/* Chips de método movidos pra cá só quando há >1 opção (mostra o que
                    o seller habilitou no link). Para link single-method, omitimos —
                    a escolha está implícita no que o checkout exibe na coluna direita. */}
                {link.paymentTypes.length > 1 && (
                  <div className={styles.amountMeta}>
                    {link.paymentTypes.map((t) => {
                      const methodKey = t as keyof typeof styles & string;
                      const colorClass = styles[`methodTag${methodKey}`] ?? "";
                      const activeClass = chosenMethod === t ? styles.methodTagActive : "";
                      return (
                        <span key={t} className={`${colorClass} ${activeClass}`.trim()}>
                          {paymentTypeLabel(t)}
                        </span>
                      );
                    })}
                  </div>
                )}
              </div>

              <div className={styles.panel}>
                <div className={styles.panelTitle}>
                  {stage === "method_choice"
                    ? "Como deseja pagar?"
                    : isCard
                    ? "Forma de pagamento"
                    : "Dados do pagador"}
                </div>

              {stage === "method_choice" && (
                <div className={styles.methodChoice}>
                  {link.paymentTypes.map((t) => {
                    const meta = METHOD_DISPLAY[t] ?? { label: t, description: "", icon: null };
                    return (
                      <button
                        type="button"
                        key={t}
                        className={styles.methodOption}
                        onClick={() => {
                          setChosenMethod(t);
                          const card = t === "CREDIT_CARD" || t === "DEBIT_CARD";
                          setStage(card ? "card_init" : "brl_form");
                        }}
                      >
                        <span className={styles.methodIcon} aria-hidden="true">{meta.icon}</span>
                        <span className={styles.methodLabel}>{meta.label}</span>
                        <span className={styles.methodDesc}>{meta.description}</span>
                        <span className={styles.methodArrow} aria-hidden="true">
                          <svg width="16" height="16" viewBox="0 0 24 24" fill="none">
                            <path d="M9 6l6 6-6 6" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round" />
                          </svg>
                        </span>
                      </button>
                    );
                  })}
                </div>
              )}

              {isCard && stage === "card_init" && (
                <div className={styles.loadingCard} role="status" aria-live="polite">
                  <div className={styles.spinner} aria-hidden="true" />
                  <p>Preparando pagamento seguro…</p>
                </div>
              )}

              {isCard && stage === "card_ready" && elementsOptions && stripePromise && (
                <Elements stripe={stripePromise} options={elementsOptions}>
                  {errorMessage && <div className={styles.alertError} role="alert">{errorMessage}</div>}
                  <CardCheckout
                    token={token}
                    amount={link.amount}
                    chosenMethod={chosenMethod ?? "CREDIT_CARD"}
                    onPaid={(result) => {
                      setPaymentResult(result);
                      setStage("success");
                    }}
                    onError={(msg) => setErrorMessage(msg)}
                  />
                </Elements>
              )}

              {(isPix || isBoleto) && (stage === "brl_form" || stage === "brl_submitting") && (
                <BrlPayerForm
                  onSubmit={handleBrlSubmit}
                  submitting={stage === "brl_submitting"}
                  errorMessage={errorMessage}
                />
              )}

              {(isPix || isBoleto) && stage === "brl_awaiting" && paymentResult && (
                <>
                  {isPix && (
                    <PixInstrument
                      qr={paymentResult.payment.pixQrCode}
                      image={paymentResult.payment.pixImageUrl}
                    />
                  )}
                  {isBoleto && <BoletoInstrument url={paymentResult.payment.boletoUrl} />}
                </>
              )}
              </div>
            </div>
          </>
        )}

        <p className={styles.footer}>
          Processado por <a href="https://fellowpay.com.br">Fellow Pay</a>
        </p>
      </main>
    </div>
  );
}
