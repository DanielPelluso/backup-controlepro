# Backup ControlePro

Aplicativo Windows portátil para **backup e restauração** do banco de dados e das
**fotos (Supabase Storage)** do projeto ControlePro.

Baseado no `backup-timerank`: EXE único, sem instalar .NET, com o cliente
PostgreSQL na subpasta `pg\`.

## O que o backup cobre

| Parte | Conteúdo |
|---|---|
| Banco | schemas `public`, `auth`, `storage`, `supabase_migrations` (schema + dados + formato custom) |
| Fotos | todos os arquivos dos buckets do Supabase Storage, com manifesto SHA-256 |

Cada execução cria uma pasta ao lado do EXE:

```text
041126100822\
  schema-041126100822.sql
  data-041126100822.sql
  dump-041126100822.backup
  meta.json
  storage\
    storage-manifest.json
    photos\<caminho original>
```

- Formato da pasta: `ddMMyyHHmmss`.
- Sem retenção automática: nada é apagado.
- Se o backup das fotos falhar, o backup inteiro é marcado como inválido.

## Requisitos

- Windows x64 (10/11).
- Acesso de rede ao pooler do Supabase e à API do Storage.
- Credenciais (nunca no repo):
  - **senha do banco**: `CONTROLEPRO_SUPABASE_DB_PASSWORD` (fallback `SUPABASE_DB_PASSWORD`)
  - **token do Management API**: `CONTROLEPRO_SUPABASE_ACCESS_TOKEN` (fallback `SUPABASE_ACCESS_TOKEN`)

Na primeira execução o app pede os dois segredos e os salva em DPAPI ao lado do
EXE (`.controlepro-dbpass` e `.controlepro-access-token`). Esse arquivo só
funciona no mesmo usuário Windows daquela máquina.

## Uso

1. Copie a pasta `dist\` para a máquina Windows de destino.
2. Execute `BackupControlePro.exe`.
3. Informe senha e token na primeira vez.
4. **Fazer backup agora (banco + fotos)**.

### Restauração

Na lista, escolha o backup e marque o que restaurar:

- **Restaurar banco** + escopo `DADOS` ou `TUDO` (TUDO = schema + dados do `public`).
- **Restaurar fotos** (checkbox separado).

Antes de qualquer restore o app faz um **backup pré-restore** do estado atual
sempre do banco, e **também das fotos** se “Restaurar fotos” estiver marcado.

O restore de fotos cria buckets ausentes, faz upload com `x-upsert` e verifica
SHA-256. **Não apaga arquivos extras** no bucket.

### CLI (automação)

```text
BackupControlePro.exe --backup [--no-storage]
BackupControlePro.exe --list
BackupControlePro.exe --restore data|full <stamp> [--with-storage] --yes
```

## Construir o `dist\`

Requer .NET 10 SDK e os binários do PostgreSQL 17+ (por exemplo o `pgenv`).

```powershell
powershell -ExecutionPolicy Bypass -File build.ps1
# ou apontando para outra pasta de binários:
powershell -ExecutionPolicy Bypass -File build.ps1 -PgBin "C:\tools\pgsql\bin"
```

O script publica o EXE self-contained em `dist\` e copia o cliente PostgreSQL
para `dist\pg\`. Backups e arquivos de senha existentes em `dist\` são
preservados.

## Segurança

- Senha e token ficam em DPAPI (usuário Windows) ou em variáveis de ambiente.
- A `service_role` do Storage é obtida em runtime via Management API e **não** é
  gravada em disco, log ou `meta.json`.
- `dist\`, `bin\`, `obj\`, backups e segredos ficam fora do Git.

## Limitações

- Restauração `TUDO` limita-se ao schema `public` (auth/storage são da plataforma).
- O cliente PostgreSQL embutido é compatível com o servidor Supabase (17+).
- Sem retenção automática: monitore o espaço em disco.
