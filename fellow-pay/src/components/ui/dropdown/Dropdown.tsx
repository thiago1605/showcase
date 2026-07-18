"use client";
import type React from "react";
import { useEffect, useRef } from "react";

interface DropdownProps {
  isOpen: boolean;
  onClose: () => void;
  children: React.ReactNode;
  /** Classes do wrapper externo — posicionamento, bg, border, width, etc. */
  className?: string;
}

/**
 * Wrapper minimalista que gerencia visibilidade (isOpen) + click-outside.
 * NÃO aplica visual próprio — bg/border/rounded/shadow ficam por conta do
 * consumer via `className`. Pra glass effect, o consumer pode envelopar o
 * conteúdo (children) com `<LiquidGlassSurface>` explicitamente — assim
 * cada popup decide se quer/precisa do efeito.
 */
export const Dropdown: React.FC<DropdownProps> = ({
  isOpen,
  onClose,
  children,
  className = "",
}) => {
  const dropdownRef = useRef<HTMLDivElement>(null);

  useEffect(() => {
    const handleClickOutside = (event: MouseEvent) => {
      if (!dropdownRef.current) return;
      // Click dentro do próprio dropdown — não fecha (deixa o filho lidar).
      if (dropdownRef.current.contains(event.target as Node)) return;

      // Click em algum `.dropdown-toggle`. Antes ignorávamos QUALQUER toggle,
      // o que mantinha esse dropdown aberto quando o usuário clicava no
      // toggle de OUTRO dropdown (notification ↔ user, etc.) — bug visível
      // com dois popups abertos simultaneamente. Agora só ignoramos se o
      // toggle clicado for o NOSSO (irmão do dropdownRef, no mesmo wrapper
      // `relative` parent). Clicks em outros toggles fecham este dropdown.
      const clickedToggle = (event.target as HTMLElement).closest(
        ".dropdown-toggle"
      );
      if (clickedToggle) {
        const dropdownParent = dropdownRef.current.parentElement;
        if (dropdownParent && dropdownParent.contains(clickedToggle)) {
          // Toggle deste mesmo dropdown — onClick do toggle vai dar o handle.
          return;
        }
      }
      onClose();
    };

    document.addEventListener("mousedown", handleClickOutside);
    return () => {
      document.removeEventListener("mousedown", handleClickOutside);
    };
  }, [onClose]);


  if (!isOpen) return null;

  return (
    <div
      ref={dropdownRef}
      // `dropdown-in` aplica scale + fade-in vindo do topo-direito (próximo
      // do botão trigger), animação suave de 0.15s. Definida em globals.css.
      className={`absolute z-40 right-0 mt-2 dropdown-in ${className}`}
    >
      {children}
    </div>
  );
};
