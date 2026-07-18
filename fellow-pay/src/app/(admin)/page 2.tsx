import type { Metadata } from "next";
import React from "react";
import { DashboardMetrics } from "@/components/dashboard/DashboardMetrics";
import { RecentTransactions } from "@/components/dashboard/RecentTransactions";
import { SellerBalanceCard } from "@/components/dashboard/SellerBalanceCard";
import { PaymentMethodChart } from "@/components/dashboard/PaymentMethodChart";
import { TransactionsByStatus } from "@/components/dashboard/TransactionsByStatus";
import { UpcomingPayouts } from "@/components/dashboard/UpcomingPayouts";
import { QuickActions } from "@/components/dashboard/QuickActions";
import { VolumeTimeseriesChart } from "@/components/dashboard/VolumeTimeseriesChart";
import { TopPaymentLinks } from "@/components/dashboard/TopPaymentLinks";
import { TopCustomers } from "@/components/dashboard/TopCustomers";
import { DashboardPeriodProvider } from "@/components/dashboard/PeriodContext";
import { PeriodSelector } from "@/components/dashboard/PeriodSelector";

export const metadata: Metadata = {
  title: "Painel | Fellow Pay",
  description: "Painel do seller - Fellow Pay",
};

export default function Dashboard() {
  return (
    <DashboardPeriodProvider>
      <div className="grid grid-cols-12 gap-4 md:gap-6">
        <div className="col-span-12">
          <PeriodSelector />
        </div>

        <div className="col-span-12">
          <DashboardMetrics />
        </div>

        <div className="col-span-12 xl:col-span-8">
          <VolumeTimeseriesChart />
        </div>

        <div className="col-span-12 xl:col-span-4">
          <SellerBalanceCard />
        </div>

        <div className="col-span-12 xl:col-span-6">
          <TransactionsByStatus />
        </div>

        <div className="col-span-12 xl:col-span-6">
          <PaymentMethodChart />
        </div>

        <div className="col-span-12 xl:col-span-6">
          <TopPaymentLinks />
        </div>

        <div className="col-span-12 xl:col-span-6">
          <TopCustomers />
        </div>

        <div className="col-span-12 xl:col-span-8">
          <RecentTransactions />
        </div>

        <div className="col-span-12 xl:col-span-4">
          <UpcomingPayouts />
        </div>

        <div className="col-span-12">
          <QuickActions />
        </div>
      </div>
    </DashboardPeriodProvider>
  );
}
