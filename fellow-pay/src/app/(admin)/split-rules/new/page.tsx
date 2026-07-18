"use client";
import React, { useState } from "react";
import { useRouter } from "next/navigation";
import { splitRulesService } from "@/services/split-rules.service";
import { DeleteButton } from "@/components/ui/DeleteButton";
import { BackLink } from "@/components/ui/BackLink";
import { PageHeader } from "@/components/ui/PageHeader";
import Input from "@/components/form/input/InputField";

interface RecipientInput {
  sellerId: string;
  percentage: string;
  fixedAmount: string;
}

export default function NewSplitRulePage() {
  const router = useRouter();
  const [name, setName] = useState("");
  const [recipients, setRecipients] = useState<RecipientInput[]>([{ sellerId: "", percentage: "", fixedAmount: "" }]);
  const [isLoading, setIsLoading] = useState(false);
  const [error, setError] = useState("");

  const addRecipient = () => {
    setRecipients([...recipients, { sellerId: "", percentage: "", fixedAmount: "" }]);
  };

  const removeRecipient = (index: number) => {
    setRecipients(recipients.filter((_, i) => i !== index));
  };

  const updateRecipient = (index: number, field: keyof RecipientInput, value: string) => {
    const updated = [...recipients];
    updated[index] = { ...updated[index], [field]: value };
    setRecipients(updated);
  };

  const handleSubmit = async (e: React.FormEvent) => {
    e.preventDefault();
    setError("");

    const totalPercentage = recipients.reduce((sum, r) => sum + (parseFloat(r.percentage) || 0), 0);
    if (totalPercentage > 100) {
      setError("A soma das porcentagens não pode exceder 100%.");
      return;
    }

    setIsLoading(true);
    try {
      // Backend reads ownerSellerId from the JWT — never accept it from the body. The
      // recipients here can include the caller themselves (as a recipient) or other
      // sellers in the same tenant; backend validates each one exists.
      await splitRulesService.create({
        name,
        recipients: recipients.map((r, i) => ({
          sellerId: r.sellerId,
          percentage: parseFloat(r.percentage) || 0,
          fixedAmount: r.fixedAmount ? parseFloat(r.fixedAmount) : 0,
          priority: i + 1,
        })),
      });
      router.push("/split-rules");
    } catch (err) {
      setError(err instanceof Error ? err.message : "Erro ao criar regra de split.");
    }
    setIsLoading(false);
  };

  return (
    <div className="space-y-6 max-w-2xl">
      <BackLink fallbackHref="/split-rules" />

      <PageHeader
        title="Nova regra de split"
        subtitle="Configure como o valor será dividido entre os recebedores."
      />

      {error && <div className="p-3 rounded-lg bg-error-50 dark:bg-error-500/10 text-error-700 dark:text-error-400 text-sm">{error}</div>}

      <form onSubmit={handleSubmit} className="space-y-6">
        <div className="rounded-xl border border-gray-200 bg-white p-5 dark:border-gray-800 dark:bg-gray-900">
          <Input
            label="Nome da Regra"
            type="text"
            value={name}
            onChange={(e) => setName(e.target.value)}
            placeholder="Ex: Split 70/30 com parceiro"
            required
          />
        </div>

        <div className="rounded-xl border border-gray-200 bg-white p-5 dark:border-gray-800 dark:bg-gray-900">
          <div className="flex items-center justify-between mb-4">
            <h2 className="text-sm font-medium text-gray-900 dark:text-white">Recebedores</h2>
            <button type="button" onClick={addRecipient} className="text-xs font-medium text-brand-500 hover:text-brand-600">
              + Adicionar recebedor
            </button>
          </div>

          <div className="space-y-4">
            {recipients.map((recipient, index) => (
              <div key={index} className="p-4 rounded-lg border border-gray-100 dark:border-gray-800 space-y-3">
                <div className="flex items-center justify-between">
                  <span className="text-xs font-medium text-gray-500 dark:text-gray-400">Recebedor {index + 1}</span>
                  {recipients.length > 1 && (
                    <DeleteButton
                      onClick={() => removeRecipient(index)}
                      size="xs"
                      variant="ghost"
                      ariaLabel={`Remover recebedor ${index + 1}`}
                    />
                  )}
                </div>
                <Input
                  label="Seller ID"
                  type="text"
                  value={recipient.sellerId}
                  onChange={(e) => updateRecipient(index, "sellerId", e.target.value)}
                  placeholder="ID do seller"
                  required
                />
                <div className="grid grid-cols-2 gap-3">
                  <Input
                    label="Porcentagem (%)"
                    type="number"
                    step={0.01}
                    min="0"
                    max="100"
                    value={recipient.percentage}
                    onChange={(e) => updateRecipient(index, "percentage", e.target.value)}
                    placeholder="0"
                  />
                  <Input
                    label="Valor fixo (R$)"
                    type="number"
                    step={0.01}
                    min="0"
                    value={recipient.fixedAmount}
                    onChange={(e) => updateRecipient(index, "fixedAmount", e.target.value)}
                    placeholder="0,00"
                  />
                </div>
              </div>
            ))}
          </div>
        </div>

        <div className="flex justify-end gap-3">
          <button type="button" onClick={() => router.back()} className="rounded-lg px-4 py-2.5 text-sm font-medium text-gray-600 hover:bg-gray-100 dark:text-gray-400 dark:hover:bg-gray-800">Cancelar</button>
          <button type="submit" disabled={isLoading} className="rounded-lg bg-brand-500 px-4 py-2.5 text-sm font-medium text-white hover:bg-brand-600 disabled:opacity-50">{isLoading ? "Criando..." : "Criar Regra"}</button>
        </div>
      </form>
    </div>
  );
}
