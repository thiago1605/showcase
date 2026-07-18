import type { Metadata } from "next";
import { SettingsContent } from "@/components/settings/SettingsContent";

export const metadata: Metadata = {
  title: "Configurações | Fellow Pay",
};

export default function SettingsPage() {
  return <SettingsContent />;
}
