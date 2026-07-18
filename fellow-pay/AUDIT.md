# Fellow Pay - Auditoria de Implementação

> Portal do Seller - Grupo Fellow
> Última atualização: 2026-05-05

---

## Fase 1: Estrutura Base e Identidade Visual

- [x] Escanear projeto TailAdmin e gerar diagnóstico
- [x] Escanear Fellow Core (API/backend) para entender domínios e DTOs
- [x] Aplicar paleta Fellow Pay (roxo institucional, grays operacionais)
- [x] Criar tokens CSS/Theme (light + dark mode)
- [x] Configurar logos Fellow Pay (sidebar, header, mobile)
- [x] Trocar font para Inter
- [x] Remover branding TailAdmin
- [x] Criar navegação do portal do seller
- [x] Criar dashboard do seller (métricas mockadas)
- [x] Criar páginas placeholder profissionais para todos os módulos
- [x] Criar estrutura de API client (`lib/api/client.ts`)
- [x] Criar types/interfaces alinhados com Fellow Core (seller-scoped)
- [x] Criar services por domínio (auth, dashboard, transactions)
- [x] Criar `.env.local.example`
- [x] Refatorar portal para visão seller (remover dados internos da plataforma)
- [x] Build sem erros
- [x] README atualizado

---

## Fase 2: Autenticação JWT

- [x] Tela de login funcional (email/senha)
- [x] Fluxo MFA/TOTP (verificação em 2 fatores)
- [x] Refresh token automático (interceptor no client)
- [x] Proteção de rotas (AuthGuard client-side + middleware)
- [x] Armazenamento seguro do token (sessionStorage - memory-like)
- [x] Logout com invalidação de sessão
- [x] Tela de "Esqueci minha senha"
- [x] Tela de reset de senha

---

## Fase 3: Integração com Fellow Core API

- [x] Conectar dashboard ao `GET /api/v1/dashboard`
- [x] Conectar saldo ao `GET /api/v1/sellers/me/balance`
- [x] Conectar listagem de transações ao `GET /api/v1/transactions`
- [x] Conectar listagem de clientes ao `GET /api/v1/customers`
- [x] Conectar payment links ao `GET /api/v1/payment-links`
- [x] Conectar split rules ao `GET /api/v1/split-rules`
- [x] Conectar simulador ao `POST /api/v1/split-rules/simulate`
- [x] Conectar payouts ao `GET /api/v1/payouts`
- [x] Conectar refunds ao `GET /api/v1/transactions/{id}/refunds`
- [x] Conectar disputes (quando endpoint disponível)
- [x] Conectar assinaturas ao `GET /api/v1/subscriptions`
- [x] Conectar recibos ao `GET /api/v1/receipts/seller/{sellerId}`
- [x] Conectar webhooks ao `GET /api/v1/webhook-endpoints`
- [x] Conectar relatórios ao `GET /api/v1/scheduled-reports`

---

## Fase 4: Tabelas Funcionais

- [x] Componente de tabela reutilizável com paginação
- [x] Filtros por status, data, método de pagamento
- [x] Ordenação por coluna
- [x] Busca por texto (cliente, ID, valor)
- [x] Loading states (skeleton)
- [x] Empty states contextuais

---

## Fase 5: Páginas de Detalhe

- [x] Detalhe de transação (timeline de eventos, dados do pagador)
- [x] Ação de refund a partir do detalhe da transação
- [x] Detalhe de payout
- [x] Detalhe de subscription
- [x] Detalhe de payment link (usos, link copiável)
- [x] Detalhe de dispute (prazo, evidências)

---

## Fase 6: Ações e Formulários

- [x] Criar Payment Link (form completo)
- [x] Solicitar saque (form + confirmação)
- [x] Configurar webhook endpoint (form + seleção de eventos)
- [x] Criar/editar Split Rule (form com recipients dinâmicos)
- [x] Simulador de Split funcional (request + visualização resultado)
- [x] Criar assinatura (form com cliente + intervalo)
- [x] Criar relatório agendado (form com tipo + formato + frequência)
- [x] Convidar membro da equipe (form + seleção de role)

---

## Fase 7: Exportação e Relatórios

- [x] Exportar transações CSV via `GET /api/v1/transactions/export?format=csv`
- [x] Exportar transações PDF via `GET /api/v1/transactions/export?format=pdf`
- [x] Exportar payouts CSV/PDF
- [x] Download de recibo (PDF)
- [x] Relatórios agendados (CRUD funcional)

---

## Fase 8: UX/UI Polish

- [x] Responsividade mobile completa
- [x] Loading states / skeletons em todas as páginas
- [x] Toast notifications (sucesso, erro, info)
- [x] Confirmação modal para ações destrutivas (refund, cancelar)
- [x] Breadcrumbs nas páginas internas
- [x] Favicon Fellow Pay
- [x] Dark mode refinamento final
- [x] Animações de transição suaves

---

## Fase 9: Equipe e Permissões

- [x] CRUD de membros da equipe
- [x] Seleção de role (Owner, Financeiro, Operações, Suporte)
- [x] Controle de acesso por role no frontend (esconder/mostrar ações)
- [x] Convite por email
- [x] Remoção de membro

---

## Fase 10: Segurança e Produção

- [x] Nunca expor X-Api-Key no frontend
- [x] Validação de inputs (client-side)
- [x] Rate limiting awareness (tratar HTTP 429)
- [x] CSRF protection
- [x] Content Security Policy headers
- [x] Testes E2E (Playwright ou Cypress)
- [x] Testes unitários dos services
- [x] CI/CD pipeline (build + lint + test)
- [ ] Configuração de domínio e deploy

---

## Notas

- **Produto/frontend:** Fellow Pay
- **API/backend:** Fellow Core
- **Portal:** Portal do Seller (nunca "admin" na UI)
- **Auth futuro:** JWT Bearer (sellerId vem do token, nunca do frontend)
- **Escopo:** Seller vê apenas seus próprios dados
