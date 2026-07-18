import type { Metadata } from "next";
import { TierPageClient } from "@/components/tier/TierPageClient";

export const metadata: Metadata = {
  title: "Meu Tier | Fellow Pay",
  description: "Status de tier, taxas vigentes e progresso na Fellow Pay.",
};

export default function TierPage() {
  return <TierPageClient />;
}
