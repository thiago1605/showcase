import SignInForm from "@/components/auth/SignInForm";
import { Metadata } from "next";

export const metadata: Metadata = {
  title: "Entrar | Fellow Pay",
  description: "Acesse o portal do seller Fellow Pay",
};

export default function SignIn() {
  return <SignInForm />;
}
