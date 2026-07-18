# Showcase

Este repositório reúne uma versão pública de um gateway de pagamentos desenvolvida para demonstrar arquitetura de software, padrões de projeto e boas práticas de engenharia, preservando as informações proprietárias do projeto original.

## Estrutura do projeto

### `fellow-pay`

Aplicação **frontend** do gateway de pagamentos, desenvolvida com **Next.js**.

Responsável pela interface do usuário, experiência da aplicação e comunicação com a API do backend.

### `fellow-core`

Aplicação **backend** do gateway de pagamentos, desenvolvida em **.NET**.

Contém a API, a modelagem do domínio, integrações com serviços externos, persistência de dados, processamento assíncrono e demais componentes responsáveis pelas operações da plataforma.

## Tecnologias

### Frontend

* Next.js
* React
* TypeScript

### Backend

* .NET
* ASP.NET Core
* Entity Framework Core
* PostgreSQL
* Redis
* Hangfire

### Infraestrutura

* Docker

## Objetivo

Este repositório tem como objetivo demonstrar:

* Arquitetura de software;
* Organização em camadas;
* Domain-Driven Design (DDD);
* Clean Architecture;
* Padrões de projeto;
* Boas práticas de desenvolvimento;
* Estruturação de APIs REST;
* Organização e qualidade do código.

## Observação

Esta é uma versão **pública** do projeto.

Alguns módulos, implementações e seus respectivos testes foram intencionalmente omitidos por conterem regras de negócio proprietárias e informações confidenciais. O repositório preserva a arquitetura, a organização da solução e as práticas de engenharia utilizadas no desenvolvimento, sem expor a lógica de negócio do sistema original.

## Licença

Este projeto é disponibilizado exclusivamente para fins de demonstração técnica e composição de portfólio. Não é permitida a utilização da lógica ou da arquitetura apresentada para fins comerciais sem autorização.
