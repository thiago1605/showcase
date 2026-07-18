"use client";
import React, { createContext, useContext, useState, useCallback } from "react";

type ToastType = "success" | "error" | "info" | "warning";

interface Toast {
  id: string;
  type: ToastType;
  message: string;
}

interface ToastContextValue {
  toasts: Toast[];
  addToast: (type: ToastType, message: string) => void;
  removeToast: (id: string) => void;
}

const ToastContext = createContext<ToastContextValue | undefined>(undefined);

export function useToast() {
  const context = useContext(ToastContext);
  if (!context) throw new Error("useToast must be used within ToastProvider");
  return context;
}

export function ToastProvider({ children }: { children: React.ReactNode }) {
  const [toasts, setToasts] = useState<Toast[]>([]);

  const addToast = useCallback((type: ToastType, message: string) => {
    const id = Math.random().toString(36).slice(2);
    setToasts((prev) => [...prev, { id, type, message }]);
    setTimeout(() => {
      setToasts((prev) => prev.filter((t) => t.id !== id));
    }, 4000);
  }, []);

  const removeToast = useCallback((id: string) => {
    setToasts((prev) => prev.filter((t) => t.id !== id));
  }, []);

  return (
    <ToastContext.Provider value={{ toasts, addToast, removeToast }}>
      {children}
      <ToastContainer toasts={toasts} onRemove={removeToast} />
    </ToastContext.Provider>
  );
}

const typeStyles: Record<ToastType, string> = {
  success: "bg-success-50 border-success-200 text-success-700 dark:bg-success-500/10 dark:border-success-800 dark:text-success-400",
  error: "bg-error-50 border-error-200 text-error-700 dark:bg-error-500/10 dark:border-error-800 dark:text-error-400",
  warning: "bg-warning-50 border-warning-200 text-warning-700 dark:bg-warning-500/10 dark:border-warning-800 dark:text-warning-400",
  info: "bg-blue-light-50 border-blue-light-200 text-blue-light-700 dark:bg-blue-light-500/10 dark:border-blue-light-800 dark:text-blue-light-400",
};

function ToastContainer({ toasts, onRemove }: { toasts: Toast[]; onRemove: (id: string) => void }) {
  if (toasts.length === 0) return null;

  return (
    <div className="fixed bottom-4 right-4 z-[100] flex flex-col gap-2 max-w-sm">
      {toasts.map((toast) => (
        <div
          key={toast.id}
          className={`rounded-lg border px-4 py-3 text-sm shadow-lg animate-in slide-in-from-right ${typeStyles[toast.type]}`}
        >
          <div className="flex items-center justify-between gap-3">
            <span>{toast.message}</span>
            <button onClick={() => onRemove(toast.id)} className="opacity-60 hover:opacity-100 text-current">
              <svg width="14" height="14" viewBox="0 0 14 14" fill="none"><path d="M10.5 3.5L3.5 10.5M3.5 3.5l7 7" stroke="currentColor" strokeWidth="1.5" strokeLinecap="round"/></svg>
            </button>
          </div>
        </div>
      ))}
    </div>
  );
}
