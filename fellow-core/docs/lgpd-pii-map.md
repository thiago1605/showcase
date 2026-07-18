# FellowCore — LGPD / PII Data Map

## 1. PII Armazenada

### Sellers (sellers)
| Campo | Tipo PII | Base Legal | Retencao |
|-------|----------|------------|----------|
| legal_name | Nome empresarial | Execucao contratual | Duracao do contrato + 5 anos |
| trade_name | Nome fantasia | Execucao contratual | Duracao do contrato + 5 anos |
| document (CNPJ) | Documento fiscal | Obrigacao legal (fiscal) | Permanente |
| email | Contato | Execucao contratual | Duracao do contrato + 5 anos |
| external_account_id | Conta BaaS | Execucao contratual | Duracao do contrato + 5 anos |

### Users (users)
| Campo | Tipo PII | Base Legal | Retencao |
|-------|----------|------------|----------|
| email | Identificacao | Execucao contratual | Conta ativa + 5 anos |
| name | Nome | Execucao contratual | Conta ativa + 5 anos |
| password_hash | Credencial (hash) | Seguranca | Conta ativa |
| totp_secret (encrypted) | 2FA segredo (AES-GCM) | Seguranca | Conta ativa |

### Transactions — Payer Data (transactions)
| Campo | Tipo PII | Base Legal | Retencao |
|-------|----------|------------|----------|
| payer_name | Nome pagador | Execucao contratual | 5 anos (fiscal) |
| payer_document | CPF/CNPJ pagador | Obrigacao legal (fiscal) | 5 anos (fiscal) |
| payer_email | Email pagador | Execucao contratual | 5 anos (fiscal) |

### Subscriptions — Customer Data (subscriptions)
| Campo | Tipo PII | Base Legal | Retencao |
|-------|----------|------------|----------|
| customer_name | Nome | Execucao contratual | Duracao subscription + 5 anos |
| customer_document | CPF/CNPJ | Obrigacao legal (fiscal) | 5 anos (fiscal) |
| customer_email | Email | Execucao contratual | Duracao subscription + 5 anos |

### Login Logs (login_logs)
| Campo | Tipo PII | Base Legal | Retencao |
|-------|----------|------------|----------|
| user_id | Referencia | Seguranca / Interesse legitimo | 180 dias |
| ip_address | IP | Seguranca / Interesse legitimo | 180 dias |
| user_agent | Browser info | Seguranca / Interesse legitimo | 180 dias |

## 2. PII NAO Armazenada

- **Dados de cartao de credito**: NAO tocam o servidor FellowCore. Tokenizacao via Stripe.js client-side.
- **Senhas em texto claro**: Apenas hash (BCrypt) armazenado.
- **TOTP secrets**: Encriptados com AES-GCM (version byte 0x02).
- **API keys/secrets**: Apenas hash SHA-256 armazenado.

## 3. Politica de Retencao

| Tipo de Dado | Retencao | Justificativa |
|-------------|----------|---------------|
| Transacoes | 5 anos | Obrigacao fiscal (CTN Art. 150) |
| Ledger entries | Permanente | Auditoria financeira |
| Webhook deliveries | 90 dias | Debugging operacional |
| Outbox messages | 30 dias | Reprocessamento |
| Login logs | 180 dias | Seguranca |
| Reconciliation | 365 dias | Auditoria |
| Settlement reports | 365 dias | Auditoria |

## 4. Processo de DSR (Data Subject Request) — LGPD

### Direito de Acesso (Art. 18, II)
1. Receber solicitacao formal do titular (email verificado)
2. Exportar dados via queries SQL direcionadas (sellers, users, transactions)
3. Retornar em formato legivel (JSON/CSV) em ate 15 dias

### Direito de Eliminacao (Art. 18, VI)
1. Verificar se nao ha obrigacao legal de retencao (fiscal: 5 anos)
2. Se elegivel: anonymizar dados pessoais (nome, email → hash)
3. Manter dados fiscais obrigatorios com referencia anonimizada
4. Registrar solicitacao no log de auditoria

### Direito de Portabilidade (Art. 18, V)
1. Exportar dados em formato estruturado (JSON)
2. Incluir: transacoes, subscriptions, payer data
3. Excluir: dados internos (ledger entries, reconciliation)

## 5. Logs e PII

### Protecoes Implementadas
- Structured logging (Serilog) — campos PII nao sao logados por padrao
- CorrelationId para rastreabilidade sem expor PII
- Logs de erro podem conter transaction IDs (nao PII) mas NAO contem:
  - Nomes de pagadores
  - Documentos (CPF/CNPJ)
  - Emails de pagadores
  - Dados de cartao

### Recomendacao
- Revisar periodicamente (trimestral) se novos logs nao incluem PII
- Configurar Serilog Destructure para mascarar campos sensiveis se necessario
