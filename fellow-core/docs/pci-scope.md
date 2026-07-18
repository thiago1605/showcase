# FellowCore — PCI-DSS Scope Documentation

## 1. Visao Geral

FellowCore e um **Payment Facilitator (PayFac)** que processa pagamentos via Stripe e OpenPix. O sistema NAO armazena, processa ou transmite dados de cartao de credito diretamente.

## 2. Modelo de Tokenizacao

### Stripe (Cartao de Credito / Debito)
- **Client-side**: Stripe.js / Stripe Elements coleta dados do cartao diretamente no browser do pagador
- **Tokenizacao**: Stripe gera um `PaymentMethod` token client-side
- **Server-side**: FellowCore recebe apenas o `PaymentMethod` ID (pm_xxx) — NUNCA o numero do cartao, CVV ou data de expiracao
- **Charge**: FellowCore envia o token ao Stripe API para processar o pagamento
- **Resultado**: Dados de cartao (PAN, CVV, expiry) NUNCA tocam os servidores FellowCore

### Fluxo Checkout
```
[Browser] → Stripe.js coleta PAN/CVV → [Stripe] retorna pm_xxx token
[Browser] → Envia pm_xxx ao FellowCore API → [FellowCore] envia pm_xxx ao Stripe → [Stripe] processa
```

### OpenPix (PIX)
- Pagamentos PIX nao envolvem dados de cartao
- QR Code gerado via API OpenPix
- Nenhum dado PCI-relevant

## 3. PCI Scope

### Dados PCI que FellowCore NAO possui:
- Primary Account Number (PAN) — numero do cartao
- Cardholder name (do cartao)
- CVV/CVC
- PIN
- Expiration date
- Full magnetic stripe data
- EMV chip data

### Dados PCI-adjacent que FellowCore possui:
- `stripe_payment_method_id` (pm_xxx) — token, nao dado sensivel
- `stripe_payment_intent_id` (pi_xxx) — referencia de transacao
- `stripe_charge_id` (ch_xxx) — referencia de cobranca
- `stripe_customer_id` (cus_xxx) — referencia de cliente

Estes sao **tokens/referencias** e NAO sao dados PCI-DSS regulados.

## 4. SAQ Esperado

### SAQ A-EP (E-commerce Payment Application — Partial Outsourcing)

FellowCore se qualifica para **SAQ A-EP** porque:
- Paginas de checkout sao servidas pelo FellowCore (nao iframe puro)
- Dados de cartao sao coletados via Stripe.js client-side (JavaScript redirect/form post)
- Nenhum dado de cartao e processado, armazenado ou transmitido pelo servidor
- Dependencia total do Stripe como PCI Level 1 Service Provider

### Controles Requeridos (SAQ A-EP)
- [x] TLS 1.2+ em todas as conexoes (HTTPS obrigatorio)
- [x] Nenhum armazenamento de dados de cartao
- [x] Content Security Policy configurado
- [x] Subresource Integrity (SRI) para scripts externos
- [x] XSS prevention (escapeHtml, isSafeUrl no checkout.html)
- [x] SSRF protection no webhook delivery
- [x] API keys encriptadas/hashed em repouso

## 5. Responsabilidades

| Area | Responsavel | Notas |
|------|-------------|-------|
| Coleta de dados de cartao | Stripe | PCI Level 1 compliant |
| Tokenizacao | Stripe.js (client-side) | Nenhum dado no server |
| Processamento de pagamento | Stripe API | Via token pm_xxx |
| Armazenamento de dados de cartao | Stripe | FellowCore NAO armazena |
| Webhook delivery | FellowCore | Contém apenas referencias (ids) |
| Seguranca da aplicacao | FellowCore | TLS, auth, SSRF, XSS prevention |
| Infraestrutura | FellowCore / Cloud Provider | Firewalls, rede, acesso |

## 6. Evidencias de Conformidade

- Checkout usa `stripe.js` (v3) carregado de `https://js.stripe.com`
- Nenhuma API do FellowCore aceita PAN, CVV ou card data como parametro
- Validadores de request (FluentValidation) nao possuem campos de cartao
- Stripe secret key armazenada como environment variable, NUNCA hardcoded
- Checkout endpoint `/checkout/config` retorna apenas `stripePk` (public key) — nunca secret key

## 7. Revisao

- Frequencia: anual ou quando houver mudanca arquitetural
- Responsavel: Security Lead
- Ultima revisao: 2026-05-03
