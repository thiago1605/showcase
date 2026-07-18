"use client";

import React from "react";

/**
 * Pill com gradient duotone + sombra colorida (estilo Dokue). Substitui
 * badges retangulares cinza/translúcidas em status de operação (Active,
 * Error, Pending etc).
 *
 * Cada tone tem gradient próprio (definido em globals.css como utilities
 * pill-gradient-{tone}), sombra colorida correspondente e texto branco.
 *
 * Uso típico:
 *   <GradientBadge tone="success">Active</GradientBadge>
 *   <GradientBadge tone="error">API Token Expired</GradientBadge>
 *   <GradientBadge tone="brand" size="lg">2 produtos</GradientBadge>
 */
export type BadgeTone = "brand" | "blue" | "success" | "error" | "amber" | "neutral";
export type BadgeSize = "sm" | "md" | "lg";

const SIZE_CLS: Record<BadgeSize, string> = {
  sm: "px-2 py-0.5 text-[10px]",
  md: "px-2.5 py-1 text-xs",
  lg: "px-4 py-1.5 text-sm",
};

const TONE_CLS: Record<BadgeTone, string> = {
  brand: "pill-gradient-brand text-white",
  blue: "pill-gradient-blue text-white",
  success: "pill-gradient-success text-white",
  error: "pill-gradient-error text-white",
  amber: "pill-gradient-amber text-white",
  neutral: "bg-gray-100 text-gray-600 dark:bg-gray-800 dark:text-gray-300",
};

export function GradientBadge({
  tone = "brand",
  size = "md",
  className = "",
  children,
}: {
  tone?: BadgeTone;
  size?: BadgeSize;
  className?: string;
  children: React.ReactNode;
}) {
  return (
    <span
      className={`inline-flex items-center gap-1 rounded-full font-semibold leading-none whitespace-nowrap ${TONE_CLS[tone]} ${SIZE_CLS[size]} ${className}`}
    >
      {children}
    </span>
  );
}
