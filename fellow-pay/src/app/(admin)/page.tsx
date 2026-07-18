import type { Metadata } from "next";
import React from "react";
import { DashboardMetrics } from "@/components/dashboard/DashboardMetrics";
import { SellerBalanceCard } from "@/components/dashboard/SellerBalanceCard";
import { PaymentMethodChart } from "@/components/dashboard/PaymentMethodChart";
import { TransactionsByStatus } from "@/components/dashboard/TransactionsByStatus";
import { QuickActions } from "@/components/dashboard/QuickActions";
import { VolumeTimeseriesChart } from "@/components/dashboard/VolumeTimeseriesChart";
import { DashboardPeriodProvider } from "@/components/dashboard/PeriodContext";
import { PeriodSelector } from "@/components/dashboard/PeriodSelector";
import { PageHeader } from "@/components/ui/PageHeader";

export const metadata: Metadata = {
  title: "Painel | Fellow Pay",
  description: "Painel do seller - Fellow Pay",
};

export default function Dashboard() {
  return (
    <DashboardPeriodProvider>
      <div className="grid grid-cols-12 gap-4 md:gap-6">
        <div className="col-span-12">
          <PageHeader
            size="hero"
            title="Painel"
            subtitle="Visão geral do seu negócio. Receita, transações, saldo, performance — tudo num lugar."
            decorIcon={
              // Bento 4-quadrantes como marca d'água — PageHeader renderiza
              // gigante (w-44+) em white/10 à esquerda atrás do texto.
              <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.2" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true">
                <rect x="3" y="3" width="7" height="9" rx="1.5" />
                <rect x="14" y="3" width="7" height="5" rx="1.5" />
                <rect x="14" y="12" width="7" height="9" rx="1.5" />
                <rect x="3" y="16" width="7" height="5" rx="1.5" />
              </svg>
            }
          />
        </div>

        {/* PeriodSelector sticky: gruda no topo logo abaixo do AppHeader
            (que tem altura ~72px com py-4 lg). z-30 fica abaixo do z-99999
            do header e acima do conteúdo da grid. */}
        <div className="col-span-12 sticky top-[72px] z-30">
          <PeriodSelector />
        </div>

        <div className="col-span-12">
          <DashboardMetrics />
        </div>

        {/* Row 1: tendência temporal + posição de caixa. Resposta às
            perguntas "quanto entrou ao longo do tempo?" + "quanto eu tenho
            agora?" — andam juntas na cabeça do seller. */}
        <div className="col-span-12 xl:col-span-8">
          <VolumeTimeseriesChart />
        </div>

        <div className="col-span-12 xl:col-span-4">
          <SellerBalanceCard />
        </div>

        {/* Row 2: distribuição. TxByStatus (status das transações) e
            PaymentMethodChart (volume por método) — duas perguntas
            complementares sobre a forma do volume capturado. */}
        <div className="col-span-12 xl:col-span-6">
          <TransactionsByStatus />
        </div>

        <div className="col-span-12 xl:col-span-6">
          <PaymentMethodChart />
        </div>

        <div className="col-span-12">
          <QuickActions />
        </div>
      </div>
    </DashboardPeriodProvider>
  );
}
