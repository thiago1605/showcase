"use client";
import React from "react";

interface PagePlaceholderProps {
  title: string;
  subtitle: string;
  actionLabel?: string;
  actionHref?: string;
  emptyStateMessage: string;
  columns?: string[];
}

export function PagePlaceholder({
  title,
  subtitle,
  actionLabel,
  emptyStateMessage,
  columns = [],
}: PagePlaceholderProps) {
  return (
    <div className="space-y-6">
      <div className="flex items-center justify-between">
        <div>
          <h1 className="text-xl font-semibold text-gray-900 dark:text-white">
            {title}
          </h1>
          <p className="text-sm text-gray-500 dark:text-gray-400 mt-1">
            {subtitle}
          </p>
        </div>
        {actionLabel && (
          <button className="inline-flex items-center gap-2 rounded-lg bg-brand-500 px-4 py-2.5 text-sm font-medium text-white hover:bg-brand-600 transition-colors">
            <svg width="16" height="16" viewBox="0 0 16 16" fill="none">
              <path d="M8 3.333v9.334M3.333 8h9.334" stroke="currentColor" strokeWidth="1.5" strokeLinecap="round"/>
            </svg>
            {actionLabel}
          </button>
        )}
      </div>

      <div className="rounded-3xl border border-gray-200/60 bg-white dark:border-gray-800 dark:bg-gray-900">
        {columns.length > 0 && (
          <div className="border-b border-gray-200 dark:border-gray-800 px-5 py-3">
            <div className="flex items-center gap-6">
              {columns.map((col) => (
                <span
                  key={col}
                  className="text-xs font-medium text-gray-500 dark:text-gray-400 uppercase tracking-wider"
                >
                  {col}
                </span>
              ))}
            </div>
          </div>
        )}
        <div className="flex flex-col items-center justify-center py-16 px-5">
          <div className="w-12 h-12 rounded-full bg-gray-100 dark:bg-gray-800 flex items-center justify-center mb-4">
            <svg width="24" height="24" viewBox="0 0 24 24" fill="none" className="text-gray-400 dark:text-gray-500">
              <path d="M12 6v12M6 12h12" stroke="currentColor" strokeWidth="1.5" strokeLinecap="round"/>
            </svg>
          </div>
          <p className="text-sm text-gray-600 dark:text-gray-400 text-center max-w-md">
            {emptyStateMessage}
          </p>
        </div>
      </div>
    </div>
  );
}
