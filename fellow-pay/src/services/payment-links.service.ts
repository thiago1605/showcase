import { api } from "@/lib/api/client";
import { paymentTypeIndex } from "@/lib/formatters/enums";
import type { PaymentLink } from "@/types";

interface CreatePaymentLinkRequest {
  amount: number;
  /** Either the key ("PIX") or the int index (2). Service converts to int before posting. */
  paymentType: string | number;
  /** Lista de métodos aceitos. Quando set, link aceita qualquer um destes. */
  paymentTypes?: (string | number)[];
  installments?: number;
  description: string;
  maxUses?: number;
  expiresAt?: string;
  /** Optional split rule (owner: caller). Backend validates ownership + IsActive. */
  splitRuleId?: string;
  /**
   * Modelo Híbrido: override per-link da antecipação automática.
   *   - undefined/null: TX herda do flag global do seller
   *   - true: força ADVANCE (seller recebe D+30 com fee)
   *   - false: força INSTALLMENT (parcelas mensais clássicas)
   */
  advanceOptIn?: boolean | null;
}

export const paymentLinksService = {
  /**
   * Backend `GET /api/v1/payment-links` returns the seller's links as a non-paginated
   * array (filtered by SellerId from the JWT). Pagination is done client-side until the
   * volume requires moving the filter to the repository (P1 follow-up).
   */
  async list(): Promise<PaymentLink[]> {
    return api.get<PaymentLink[]>("/api/v1/payment-links");
  },

  async getById(id: string): Promise<PaymentLink> {
    return api.get<PaymentLink>(`/api/v1/payment-links/${id}`);
  },

  async create(data: CreatePaymentLinkRequest): Promise<PaymentLink> {
    // Backend PaymentType é int enum sem JsonStringEnumConverter; convertemos aqui.
    const ptIdx = paymentTypeIndex(data.paymentType);
    if (ptIdx === null) {
      throw new Error(`Método de pagamento inválido: ${data.paymentType}`);
    }
    let paymentTypesIdx: number[] | undefined;
    if (data.paymentTypes && data.paymentTypes.length > 0) {
      paymentTypesIdx = data.paymentTypes.map((t) => {
        const i = paymentTypeIndex(t);
        if (i === null) throw new Error(`Método de pagamento inválido: ${t}`);
        return i;
      });
    }
    return api.post<PaymentLink>("/api/v1/payment-links", {
      ...data,
      paymentType: ptIdx,
      paymentTypes: paymentTypesIdx,
    });
  },

  async deactivate(id: string): Promise<void> {
    return api.patch<void>(`/api/v1/payment-links/${id}/deactivate`, {});
  },

  /**
   * Edita campos não-financeiros do link: descrição, maxUses (null = ilimitado),
   * expiresAt e splitRuleId. Não permite mudar amount/paymentType — isso quebraria
   * snapshots de transações já criadas via esse link.
   */
  async update(
    id: string,
    data: {
      description?: string;
      maxUses?: number | null;
      expiresAt?: string | null;
      splitRuleId?: string | null;
      /** Quando informado, atualiza a lista de métodos aceitos (1..4). */
      paymentTypes?: (string | number)[];
      /**
       * Override per-link da antecipação automática:
       *   - undefined: NÃO altera (estado atual preservado)
       *   - null: reset pra inherit (TX vai herdar do seller). REQUER `advanceOptInReset: true` no payload.
       *   - true/false: força ADVANCE / INSTALLMENT
       */
      advanceOptIn?: boolean | null;
      /** Quando true E advanceOptIn=null, o backend reseta o flag pra null/inherit. */
      advanceOptInReset?: boolean;
    },
  ): Promise<PaymentLink> {
    let paymentTypesIdx: number[] | undefined;
    if (data.paymentTypes && data.paymentTypes.length > 0) {
      paymentTypesIdx = data.paymentTypes.map((t) => {
        const i = paymentTypeIndex(t);
        if (i === null) throw new Error(`Método de pagamento inválido: ${t}`);
        return i;
      });
    }
    return api.patch<PaymentLink>(`/api/v1/payment-links/${id}`, {
      ...data,
      paymentTypes: paymentTypesIdx,
    });
  },

  /**
   * Public endpoints — the customer-facing checkout page calls these without auth.
   * The backend marks them [AllowAnonymous].
   */
  /** Public, anonymous. Returns presentation-only fields for the checkout page. */
  async resolve(token: string): Promise<{
    amount: number;
    /** Método "primário"/default. Backward-compat. */
    paymentType: string;
    /** Lista de métodos aceitos. Sempre ≥1. Quando >1, checkout mostra seletor. */
    paymentTypes: string[];
    installments: number;
    description: string | null;
    sellerName: string | null;
  }> {
    return api.get(`/api/v1/payment-links/pay/${token}`);
  },

  /**
   * For card-typed links the payer payload is optional — pass `{}` (or just CPF) to
   * have the backend create the PaymentIntent eagerly so the Stripe Element can render
   * wallets-first without a pre-fill form. Pix/Boleto links must include name + document
   * + email; the service-side guard rejects partial payloads for those rails.
   */
  async pay(
    token: string,
    payer: {
      payerName?: string;
      payerDocument?: string;
      payerEmail?: string;
      payerPhone?: string;
      /** Em links multi-método, o cliente precisa informar qual método escolheu. */
      chosenPaymentType?: string | number;
      /** Parcelas escolhidas (modo sem juros). Omitir = usa default do link. */
      chosenInstallments?: number;
    } = {},
  ): Promise<PayResult> {
    let chosenIdx: number | undefined;
    if (payer.chosenPaymentType !== undefined) {
      const i = paymentTypeIndex(payer.chosenPaymentType);
      if (i === null) throw new Error(`Método inválido: ${payer.chosenPaymentType}`);
      chosenIdx = i;
    }
    return api.post<PayResult>(`/api/v1/payment-links/pay/${token}`, {
      ...payer,
      chosenPaymentType: chosenIdx,
    });
  },

  /** Public, anonymous: returns Stripe publishable key for the checkout page. */
  async getCheckoutConfig(): Promise<{ stripePk: string; sellerId: string | null }> {
    return api.get(`/checkout/config`);
  },

  /**
   * Public, anonymous polling. Returns the transaction's status string (e.g.
   * "CAPTURED", "PROCESSING") and an `isTerminal` flag so the frontend can stop
   * polling once the rail has reached a final state. Tenant scope is enforced
   * server-side by matching the transaction to the link's tenant.
   */
  async getTransactionStatus(token: string, transactionId: string): Promise<{ status: string; isTerminal: boolean }> {
    return api.get(`/api/v1/payment-links/pay/${token}/status/${transactionId}`);
  },

  /**
   * Opções de parcelamento "sem juros" disponíveis pro link. Retorna [] quando o
   * link não aceita crédito ou o seller não tem cap > 1. UI usa pra montar dropdown.
   * `total` é sempre = amount no modo sem juros (comprador paga o mesmo).
   */
  async installmentOptions(
    token: string,
  ): Promise<Array<{ count: number; perInstallmentAmount: number; total: number }>> {
    return api.get(`/api/v1/payment-links/pay/${token}/installments`);
  },
};

/**
 * Mirrors backend `TransactionResponseDto` + nested `GatewayPaymentDetails`.
 * `payment` carries the renderable instrument depending on PaymentType:
 *   - PIX        → pixQrCode + pixImageUrl
 *   - BOLETO     → boletoUrl
 *   - CREDIT_CARD/DEBIT_CARD → clientSecret (consumed by Stripe Elements)
 */
export interface PayResult {
  internalId: string;
  status: number;
  amount: number;
  payment: {
    transactionId: string;
    boletoUrl: string | null;
    pixQrCode: string | null;
    pixImageUrl: string | null;
    clientSecret: string | null;
    connectedAccountId: string | null;
  };
}
