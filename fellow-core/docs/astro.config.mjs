// @ts-check
import { defineConfig } from 'astro/config';
import starlight from '@astrojs/starlight';

export default defineConfig({
	integrations: [
		starlight({
			title: 'Fellow Core',
			description: 'A API de pagamentos que conecta, autoriza e escala.',
			defaultLocale: 'pt-br',
			locales: { 'pt-br': { label: 'Português', lang: 'pt-BR' } },
			logo: {
				src: './src/assets/fellow_core_full_logo_no_bg.png',
				replacesTitle: true,
			},
			social: [{ icon: 'github', label: 'GitHub', href: 'https://github.com/thiagoreis/fellow-pay' }],
			customCss: ['./src/styles/custom.css'],
			sidebar: [
				{
					label: 'Fellow Core — Developer Hub',
					items: [
						{ label: 'Introdução', slug: 'getting-started/introduction' },
						{ label: 'Quickstart', slug: 'getting-started/quickstart' },
						{ label: 'Autenticação', slug: 'getting-started/authentication' },
					],
				},
				{
					label: 'API Reference',
					items: [
						{ label: 'Tenants', slug: 'api/tenants' },
						{ label: 'Transações', slug: 'api/transactions' },
						{ label: 'Pix', slug: 'api/pix' },
						{ label: 'Links de Pagamento', slug: 'api/payment-links' },
						{ label: 'Sellers (Sub-contas)', slug: 'api/sellers' },
						{ label: 'Clientes', slug: 'api/customers' },
						{ label: 'Saques (Payouts)', slug: 'api/payouts' },
						{ label: 'Assinaturas', slug: 'api/subscriptions' },
						{ label: 'Webhook Endpoints', slug: 'api/webhook-endpoints' },
						{ label: 'Dashboard', slug: 'api/dashboard' },
						{ label: 'Exportação de Dados', slug: 'api/export' },
						{ label: 'Relatórios Agendados', slug: 'api/scheduled-reports' },
						{ label: 'Logs de Auditoria', slug: 'api/audit-logs' },
						{ label: 'Usuários', slug: 'api/users' },
						{ label: 'Autenticação (Dashboard)', slug: 'api/auth' },
					],
				},
				{
					label: 'Webhooks',
					items: [
						{ label: 'Recebendo Webhooks', slug: 'webhooks/receiving' },
						{ label: 'Eventos Disponíveis', slug: 'webhooks/events' },
						{ label: 'Segurança (HMAC)', slug: 'webhooks/security' },
					],
				},
				{
					label: 'Conceitos',
					items: [
						{ label: 'Modelo BaaS', slug: 'concepts/baas-model' },
						{ label: 'Taxas e Split', slug: 'concepts/fees-and-split' },
						{ label: 'Ciclo de Vida da Transação', slug: 'concepts/transaction-lifecycle' },
						{ label: 'Concorrência e Idempotência', slug: 'concepts/concurrency' },
					],
				},
				{
					label: 'Exemplos',
					items: [
						{ label: 'cURL', slug: 'examples/curl' },
						{ label: 'Node.js / TypeScript', slug: 'examples/nodejs' },
					],
				},
				{
					label: 'Fellow Pay — Merchant Hub',
					items: [
						{ label: 'Visão Geral do Produto', slug: 'merchant/overview' },
					],
				},
			],
			components: {
				Footer: './src/components/Footer.astro',
			},
		}),
	],
});
