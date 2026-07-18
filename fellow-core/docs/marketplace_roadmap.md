# Marketplace Roadmap — Fellow Pay

Status do produto marketplace estilo Kirvano (produto digital → afiliação → checkout
público com split automático). Lista de gaps em relação a um marketplace completo,
agrupada por criticidade.

> **Status atual** (em ordem): cada item é atualizado conforme conclusão. Itens
> marcados com ✅ estão completos e commitados. 🚧 em progresso. ⏳ pendente.

## 🔴 Bloco 1 — Crítico (destrava lançamento)

### 1. Email de entrega pós-captura — ✅ (2026-05-21)
Buyer paga (PIX/cartão/boleto) → recebe email com `Product.DeliveryUrl`. Sem isso
o ciclo de entrega quebra: cliente pagou e não tem como acessar o produto digital.

**Escopo**:
- Hook em `WebhooksService` (ou via domain event) ao detectar captura de TX com
  `ExternalReferenceId = "product:{id}"`.
- Lookup do `Product.DeliveryUrl` + `Product.Name` + buyer email
  (`Transaction.PayerEmail` — existe via migração `AddPayerInfoAndReceiptEmailTracking`).
- Email via `ResendEmailProvider` com template específico de marketplace
  (assunto, body com link + instruções básicas).
- Retry leve já coberto pelo `ResendEmailProvider` (3 attempts, exponential backoff).
- Para produto sem `DeliveryUrl` (físico/serviço): email de confirmação simples
  sem link de download.

**Esforço**: 3–4h.

### 2. UTM tracking no checkout público — ✅ (2026-05-21)
Capturar `utm_source`, `utm_campaign`, `utm_medium`, `utm_content`, `utm_term`,
`gclid`, `fbclid` na URL do `/p/{slug}` e persistir em `Transaction.Metadata`
(JSON column). Permite afiliado / produtor saberem qual campanha converteu.

**Escopo**:
- Front: capturar na URL no mount da página `/p/[slug]`, passar no body do
  `checkoutProduct()`.
- Back: aceitar no `PublicCheckoutRequest`, persistir em `Transaction.Metadata`
  (já existe campo).
- Eventualmente: query/agregação por UTM no dashboard (fase futura).

**Esforço**: 3h.

### 3. Cartão + Boleto no checkout público — ✅ (2026-05-21)
Hoje `/p/[slug]` só processa PIX. Backend já suporta cartão (Stripe) e boleto
(Stripe + OpenPix). Falta wire-up no front + handling auth-less da Stripe Elements.

**Escopo**:
- Stripe Payment Element no `/p/[slug]` (auth-less com Stripe Connect destination
  charge — usa `clientSecret` retornado pelo backend).
- Backend: `PublicCheckoutController` precisa criar `PaymentIntent` no Stripe
  com `destination` apontando pra Stripe account do seller (já configurado em
  `StripeCardRail`).
- Boleto: opcional, pode ficar pra fase 2 (volume é menor, PIX cobre quase tudo).

**Esforço**: 1–2 dias (Stripe Elements auth-less é o trabalhoso).

## 🟡 Bloco 2 — Forte (completa experiência)

### 4. Métricas por afiliado (TPV, ganhos, conversão) — ✅ (2026-05-21) — sem conversão (depende de #6)
Afiliado precisa ver performance própria pra continuar engajado. Sem dashboard
de performance, ele desliga após primeiras tentativas sem feedback.

**Escopo**:
- Endpoint `GET /api/v1/affiliations/{id}/stats`: TPV gerado, ganhos liquidados
  e a liquidar, # vendas (30d, 90d, all-time), produto mais vendido.
- Agregação: `SUM(TransactionSplits.Amount) WHERE RecipientSellerId = me`
  com filtros de data.
- Conversão real precisa de tracking de clicks (item 5 abaixo).

**Esforço**: 1 dia (sem conversão) / 2 dias (com).

### 5. Dashboard do afiliado — ✅ (2026-05-21)
Visão centralizada em `/affiliations/[id]` ou `/affiliate-marketplace/my`:
- Produtos afiliados (lista)
- Link de divulgação de cada (com tracking code)
- Ganhos por produto + total
- Próximo payout estimado
- Métricas do item 4 embutidas

**Esforço**: 1 dia (reaproveitando endpoints existentes).

### 6. Click tracking + conversão real — ✅ (2026-05-21)
Pra calcular conversão (= vendas / clicks) precisa logar visitas com `?aff=`.
Entity nova `AffiliateClickEvent` com IP, user-agent, fingerprint, timestamp,
referrer. Endpoint público `POST /api/v1/public/track-click`. Anti-spam via
dedup por (affiliateId, fingerprint, hour).

**Esforço**: 1 dia.

## 🟢 Bloco 3 — Diferencial competitivo

### 7. Pixel tracking (Facebook + Google) — ✅ (2026-05-21)
Produtor configura `FacebookPixelId` + `GoogleAdsConversionId` no produto
(`Product` entity). Front injeta script no checkout + dispara evento `Purchase`
no thank-you page. Afiliados que rodam ads pagos PRECISAM disso.

**Esforço**: 4–6h.

### 8. Materiais de divulgação — ✅ (2026-05-21)
Produtor faz upload de banners / copies / vídeos por produto. Afiliado baixa
do dashboard. Reduz fricção pra afiliado começar a divulgar.

**Escopo**:
- Entity `ProductAsset` (id, productId, type, title, url, sizeBytes).
- Endpoints CRUD pro produtor.
- Endpoint listagem pro afiliado.
- UI no `/products/[id]` aba "Materiais" + no dashboard do afiliado.
- Reusa `StorageController` que já existe.

**Esforço**: 1 dia.

### 9. Leaderboard de afiliados — ✅ (2026-05-21)
Tab `/products/{id}/affiliates` mostrando ranking dos top performers por TPV
gerado no produto. Gamification leve, aumenta engajamento.

**Esforço**: 4h (query + UI).

### 10. Co-produção (split rule × affiliate) — ✅ (2026-05-21)
Produto pode ter `Product.SplitRuleId` configurado (split de co-produção
existente: ex: 70% produtor / 30% sócio). Quando afiliado vende, comissão dele
sai DO LADO DO PRODUTOR (não dos co-produtores). Composição via
`SplitCalculationService`.

**Esforço**: 2 dias (complexo no calculation service).

### 11. Cupons de desconto — ✅ (2026-05-21) — CRUD producer + UI tab "Cupons" no editor + apply public
Entity `Coupon` (code, productId/global, type=percent|fixed, value, validity,
maxUses). Buyer aplica no checkout → preço ajusta → split recalcula proporcional.

**Esforço**: 2 dias.

## ⚪ Bloco 4 — Nice-to-have (v2+)

- ✅ **Order bumps / upsells** no checkout — (2026-05-27)
  Producer configura bumps por produto (max 3 ativos). Buyer marca no
  `/p/{slug}`, total atualiza dinâmico, captura cria N TransactionItems.
  Cupom não incide sobre bumps; splits distribuem proporcional sobre total.
- ⏳ **Multi-level affiliate** (afiliado de afiliado, comissão override)
- ⏳ **Refund self-service** (buyer pede reembolso direto, dispara fluxo
  PROCESSANDO → APROVADO/NEGADO pelo produtor)
- ⏳ **Anti-fraude robusto** (rate limit por IP/email/device fingerprint no
  checkout; bloqueio de cartões/CPFs em blocklist)
- ⏳ **Página de obrigado customizada** por produto (upsell + cross-sell)
- ✅ **Webhooks pro produtor** — (2026-05-27)
  WebhookEndpoint estendido com SellerId nullable (NULL = tenant-wide
  platform, preenchido = producer scope). Producer integra com RD Station,
  ActiveCampaign, Mailchimp. UI dedicada em `/integrations`. Payload
  enriquecido com customer/product/affiliate/utm. Reaproveita infra de
  dispatch/retry/idempotência do tenant webhook.
- ⏳ **API pública pra produtores** (endpoints com token específico do produtor pra
  integração programática)
- ⏳ **BI / cohort analysis** (retenção de buyers, LTV por cohort, etc.)

## Convenções

- Cada item é entregue em PR/commit separado quando possível, com mensagem
  `feat(marketplace): <descrição>`.
- Itens que tocam vários módulos (frontend + backend + migração) podem ir em
  commit único se conceitualmente atômicos.
- Esse arquivo é atualizado a cada conclusão — checkbox vira ✅ e nota o commit
  hash + data.

---

**Última atualização**: 2026-05-27

- **Blocos 1, 2 e 3 — completos** (11/11 itens entregues em 2026-05-21).
- **Bloco 4 — 2/8 entregues** em 2026-05-27 (order bumps + webhooks
  producer). Os 2 com maior demanda comercial atacados primeiro.
- **Pendentes do Bloco 4** (6 itens): multi-level affiliate, refund
  self-service, anti-fraude robusto, página de obrigado customizada,
  API pública para produtores, BI/cohort analysis.
- Histórico: 2026-05-26 footer atualizado; 2026-05-21 criação do roadmap.
