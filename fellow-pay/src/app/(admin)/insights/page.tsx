import type { Metadata } from "next";
import React from "react";
import { DashboardPeriodProvider } from "@/components/dashboard/PeriodContext";
import { PeriodSelector } from "@/components/dashboard/PeriodSelector";
import { HeatmapChart } from "@/components/dashboard/HeatmapChart";
import { ConversionByMethodChart } from "@/components/dashboard/ConversionByMethodChart";
import { TopPeakHours } from "@/components/dashboard/TopPeakHours";
import { TicketDistributionChart } from "@/components/dashboard/TicketDistributionChart";
import { CustomerRetentionWidget } from "@/components/dashboard/CustomerRetentionWidget";
import { PeriodComparisonChart } from "@/components/dashboard/PeriodComparisonChart";
import { TopProducts } from "@/components/dashboard/TopProducts";
import { TopPaymentLinks } from "@/components/dashboard/TopPaymentLinks";
import { TopCustomers } from "@/components/dashboard/TopCustomers";
import { PageHeader } from "@/components/ui/PageHeader";

export const metadata: Metadata = {
  title: "Insights | Fellow Pay",
  description: "Análises temporais e exploratórias da sua conta",
};

export default function InsightsPage() {
  return (
    <DashboardPeriodProvider>
      <div className="space-y-6">
        <PageHeader
          size="hero"
          title="Insights"
          subtitle="Análises exploratórias além dos KPIs do painel principal. Heatmap, conversão por método, retenção e mais."
          decorIcon={
            // 4 barras crescentes como marca d'água — analytics symbol.
            <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.2" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true">
              <line x1="4" y1="20" x2="4" y2="14" />
              <line x1="10" y1="20" x2="10" y2="10" />
              <line x1="16" y1="20" x2="16" y2="6" />
              <line x1="22" y1="20" x2="22" y2="3" />
            </svg>
          }
        />

        {/* Sticky no topo logo abaixo do AppHeader (altura ~72px). z-30
            fica abaixo do header (z-99999) e acima do conteúdo da grid. */}
        <div className="sticky top-[72px] z-30">
          <PeriodSelector showExport={false} />
        </div>

        <div className="grid grid-cols-12 gap-4 md:gap-6">
          <div className="col-span-12">
            <PeriodComparisonChart />
          </div>

          <div className="col-span-12 xl:col-span-8">
            <HeatmapChart />
          </div>

          <div className="col-span-12 xl:col-span-4">
            <TopPeakHours />
          </div>

          <div className="col-span-12 xl:col-span-8">
            <TicketDistributionChart />
          </div>

          <div className="col-span-12 xl:col-span-4">
            <CustomerRetentionWidget />
          </div>

          <div className="col-span-12">
            <ConversionByMethodChart />
          </div>

          {/* Rankings — "quem/o que está movendo a agulha". Foram movidos do
              painel principal (que ficou só com KPIs e distribuições) pra cá,
              que é o lar natural de drill-down: produto que mais vende,
              link que mais converte, cliente que mais compra. No XL ficam em
              uma linha de 3 col-4 cada; em telas menores empilham. */}
          <div className="col-span-12 xl:col-span-4">
            <TopProducts />
          </div>

          <div className="col-span-12 xl:col-span-4">
            <TopPaymentLinks />
          </div>

          <div className="col-span-12 xl:col-span-4">
            <TopCustomers />
          </div>
        </div>
      </div>
    </DashboardPeriodProvider>
  );
}
