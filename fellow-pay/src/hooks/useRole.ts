"use client";
import { useAuth } from "@/context/AuthContext";
import type { UserRole } from "@/types";

const rolePermissions: Record<UserRole, string[]> = {
  OWNER: ["*"],
  DEVELOPER: ["transactions", "customers", "payment-links", "webhooks", "split-rules", "split-simulator", "settings"],
  FINANCE: ["transactions", "customers", "payouts", "receipts", "reports", "subscriptions"],
  SUPPORT: ["transactions", "customers", "disputes", "refunds"],
  VIEWER: ["transactions", "customers"],
};

export function useRole() {
  const { user } = useAuth();
  const role = (user?.role || "VIEWER") as UserRole;

  const hasAccess = (module: string): boolean => {
    const permissions = rolePermissions[role];
    if (!permissions) return false;
    if (permissions.includes("*")) return true;
    return permissions.includes(module);
  };

  const canManageTeam = role === "OWNER";
  const canExport = ["OWNER", "FINANCE"].includes(role);
  const canRefund = ["OWNER", "FINANCE", "SUPPORT"].includes(role);
  const canCreatePaymentLink = ["OWNER", "DEVELOPER", "FINANCE"].includes(role);
  const canRequestPayout = ["OWNER", "FINANCE"].includes(role);

  return { role, hasAccess, canManageTeam, canExport, canRefund, canCreatePaymentLink, canRequestPayout };
}
