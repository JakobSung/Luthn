# 로컬 개발

[English](local-development.md)

## 사전 준비

- solution target framework와 맞는 .NET SDK
- PostgreSQL 직접 호스팅 경로를 위한 Docker

일반 사용자는 [설치 안내](installation.ko.md)를 사용하세요. 아래 명령은 원본 저장소와 .NET SDK가 필요한 기여자용입니다.

## 원본 기반 한 번에 설치

```bash
./scripts/install-local.sh
```

필요하면 `.env.example`에서 `.env`를 만들고, package restore, solution build, PostgreSQL 시작, migration, 공개 안전 예제 자료 입력, API 시작을 수행합니다. 운영자 화면은 <http://localhost:8080/>입니다.

```bash
./scripts/check-local.sh
./scripts/install-local.sh testing
./scripts/reset-local.sh --yes
```

`testing`은 PostgreSQL 없이 자격 증명 없는 memory 내 API를 준비합니다. `reset-local.sh --yes`는 로컬 PostgreSQL·운영자 화면 Docker volume을 삭제합니다.

## Build와 시험

```bash
dotnet build Luthn.sln
dotnet test Luthn.sln
```

## 로컬 memory 시험 mode로 API 실행

```bash
DOTNET_ENVIRONMENT=Testing dotnet run --project src/Luthn.Host.Api/Luthn.Host.Api.csproj --urls http://127.0.0.1:5089
```

운영자 화면은 <http://127.0.0.1:5089/>입니다. health/readiness, 읽기 전용 에이전트 연결 상태, 분류 미리 보기, 통제된 source intake, 민감 접근 요청 검토·승인·거절, 메타데이터 감사 조회를 제공합니다. 에이전트 설치·재설정·연결 해제는 host CLI에서 수행합니다.

## Docker 직접 호스팅 stack

```bash
docker compose up --build
curl http://localhost:8080/healthz
curl http://localhost:8080/readyz
```

`/healthz`는 생존 여부만 확인하고 PostgreSQL을 조회하지 않습니다. `/readyz`는 database와 최초 설정을 확인합니다. 저장소 Compose의 기본 분류 상태는 명시적인 `unconfigured`이며, 운영 환경에서는 활성 서비스 token이나 실제 분류 provider가 없으면 준비 완료가 아닙니다.

## 운영 서비스 Token

`Luthn:Auth:RequireServiceToken=true`와 외부 설정의 SHA-256 digest로 보호 API에 bearer token을 요구할 수 있습니다. 원본 token이나 실제 운영 digest를 커밋하지 않습니다.

```bash
printf '%s' "$LUTHN_SERVICE_VALUE" \
  | dotnet run --project src/Luthn.Tools -- token-digest --stdin
```

`X-Luthn-Operator`는 감사 actor를 구분하는 선택적 메타데이터이며 권한을 주지 않습니다. 지원 scope는 `agent.read`, `agent.write.summary`, `agent.connection.read`, `agent.connection.write`, `classification.preview`, `config.write`, `external-publication.read`, `external-publication.write`, `source.write`, `memory.read`, `memory.write`, `access.request`, `access.decide`, `audit.read`, `metrics.read`, `metrics.write`, 운영자용 `*`입니다.

새 설치의 기본 identity 경계는 기존과 호환되는 단일 owner입니다.

```bash
Luthn__Identity__Mode=SingleOwner
Luthn__Identity__SingleOwnerUserId=local-owner
Luthn__Auth__Tokens__0__UserId=local-owner
Luthn__Auth__Tokens__0__IsOperator=false
```

로컬 multi-user 배포는 mode를 바꾸고 모든 비운영자 product token에 하나의 제한된 user
ID를 연결합니다. ID는 소문자로 정규화하며 첫 글자는 영문자·숫자여야 하고 전체 길이는
128자 이하입니다. 허용 문자는 영문자, 숫자, `.`, `_`, `:`, `@`, `-`입니다. binding이
없거나 잘못되면 `503`을 반환하며 caller JSON으로 덮어쓸 수 없습니다.

```bash
Luthn__Identity__Mode=MultiUser
Luthn__Auth__Tokens__0__UserId=alice
Luthn__Auth__Tokens__0__IsOperator=false
Luthn__Auth__Tokens__1__Name=local-operator
Luthn__Auth__Tokens__1__IsOperator=true
```

user 또는 connector마다 별도 최소권한 token을 사용합니다. `IsOperator=true`는 명시적인
교차-owner 관리 역할이며 `X-Luthn-Operator` header는 계속 audit metadata일 뿐 역할을
부여하지 않습니다. identity 설정 변경 뒤 `/readyz`를 확인합니다.

## 분류 Provider 설정

운영자 화면의 `/api/operator/classification-provider`에서 `Mock`, `ChatGPT API`, `Claude API`, `Google AI API`, `OpenRouter API`, `External HTTP`를 선택하고 model·API key·연결 시험을 설정할 수 있습니다. API key는 server에 저장하며 응답이나 화면에 되돌려 보내지 않습니다.

직접 제3자 LLM provider는 민감도 판정 전에 원문을 받습니다. 이 전송이 허용될 때만 사용하고, 원문을 통제된 경계에 남겨야 하면 `External HTTP`를 사용합니다. API key를 보내는 endpoint는 HTTPS여야 합니다. 배포·Compose 기본값은 `unconfigured`이고, source `Development`와 `Testing` 환경만 mock을 명시적으로 켭니다. `mock`은 결정적 keyword 분류기이므로 시험·로컬 실험 전용이며 수동 실행 시 두 값을 모두 지정해야 합니다.

```bash
Luthn__Classification__Provider=mock
Luthn__Classification__AllowMock=true
Luthn__Classification__Runtime__TimeoutSeconds=30
Luthn__Classification__Runtime__MaxAttempts=2
Luthn__Classification__Runtime__RetryDelayMilliseconds=200
```

일시적 timeout, HTTP 408/429/5xx만 재시도합니다. 측정값은 `luthn.classification_provider.attempts`, `retries`, `failures`, `luthn.safe_search.candidates`입니다.

### 분류 Golden 평가

버전이 지정된 한국어 중심 합성 corpus를 network 요청 없이 로컬 mock으로
평가합니다.

```bash
dotnet run --project src/Luthn.Tools -- classification-eval
```

같은 안정된 JSON 결과를 파일로 남길 수 있습니다.

```bash
dotnet run --project src/Luthn.Tools -- classification-eval \
  --output artifacts/classification-eval.json
```

network 요청 없이 mock 기준값과 로컬 결정론적 guard를 결합한 경로도 평가할 수
있습니다.

```bash
dotnet run --project src/Luthn.Tools -- classification-eval \
  --provider guarded-mock
```

API에 현재 설정된 분류기를 평가하려면 API를 실행하고 외부 전송 가능성을
명시적으로 허용해야 합니다. 보호 API token 값은 command line에 넣지 말고
환경 변수 이름만 전달합니다.

```bash
export LUTHN_EVAL_TOKEN='<운영자가 제공한 token>'
dotnet run --project src/Luthn.Tools -- classification-eval \
  --provider configured-api \
  --api-url http://127.0.0.1:5089 \
  --allow-external-provider \
  --token-env LUTHN_EVAL_TOKEN
```

결과에는 corpus 원문을 넣지 않고 제한된 case ID, case별 분류·저장 경로 비교,
불일치 합계만 기록합니다.

runtime은 모든 설정된 provider 결과에 로컬 secret/PII guard 버전 `1`을
결합합니다. `ExternalHttp`는 self-hosted 연결이 가능한 provider 경계이고,
provider 실패를 detector 단독 저장으로 대체하지 않습니다.

```bash
Luthn__Classification__Provider=external-http
Luthn__Classification__ExternalHttp__Endpoint=https://provider.example/classify
Luthn__Classification__ExternalHttp__CredentialEnvironmentVariable=LUTHN_PROVIDER_AUTH
Luthn__Classification__ExternalHttp__AuthHeaderName=Authorization
Luthn__OperatorConfig__Directory=/var/lib/luthn/operator
```

설정에는 자격 증명 환경 변수 이름만 두고 값은 secret manager 또는 runtime 환경에서 제공합니다. 응답은 `sensitivity`, `confidence`, `categories`, `containsSensitiveMaterial`을 반환해야 합니다.

## PostgreSQL Migration

현재 schema는 digest, 안전 요약, Core tag, 민감 기록 참조만 저장하며 원본 Vault/source 열은 만들지 않습니다.

```bash
dotnet run --project src/Luthn.Tools -- migrate-db
dotnet run --project src/Luthn.Tools -- migration-script
dotnet ef migrations add <Name> \
  --project src/Luthn.Core.Persistence/Luthn.Core.Persistence.csproj \
  --startup-project src/Luthn.Core.Persistence/Luthn.Core.Persistence.csproj \
  --context LuthnDbContext \
  --output-dir Persistence/Migrations
```

감사/control event는 현재 `PayloadVersion=1`이고, 공개 안전 위키·공유 기억 검색, 민감 접근 queue, 대상별 감사 조회용 index가 있습니다.

## 선택적 PostgreSQL 연동 시험

```bash
LUTHN_POSTGRES_TEST_CONNECTION='Host=localhost;Port=5432;Database=luthn_test;Username=luthn' \
LUTHN_POSTGRES_TEST_ALLOW_RESET=true \
dotnet test tests/Luthn.Host.Api.Tests/Luthn.Host.Api.Tests.csproj --filter PostgresIntegrationSmokeTests
```

database 이름은 `luthn_test`로 시작해야 하며 시험이 해당 database를 삭제하고 다시 만듭니다.

## Backup과 복원

```bash
docker compose exec postgres pg_dump -U luthn -d luthn -Fc > luthn.backup
docker compose exec -T postgres pg_restore -U luthn -d luthn --clean --if-exists < luthn.backup
```

backup은 저장소 밖에 두고 migration 전 생성하며, 먼저 임시 database로 복원 무결성을 확인합니다. 운영 절차는 [운영](operations.ko.md)을 참고하세요.

## 운영 Compose 주의점

제공 Compose는 로컬 확인용이며 운영 틀이 아닙니다. 운영 인증, TLS, secret 저장, 고가용성, 감시, backup 보존을 별도로 구성해야 합니다. 외부 노출 전에 PostgreSQL trust 설정을 바꾸고 migration 후 `/readyz`를 사용하세요. 직접 TLS는 `Luthn__Host__EnforceHttps`, reverse proxy 뒤에서는 `Luthn__Host__EnableForwardedHeaders`를 설정합니다.

## 도구 확인 명령

```bash
dotnet run --project src/Luthn.Tools -- preview source-1 "Public implementation note."
dotnet run --project src/Luthn.Tools -- context
dotnet run --project src/Luthn.Tools -- wiki-render
dotnet run --project src/Luthn.Tools -- migrate-db
dotnet run --project src/Luthn.Tools -- migration-script
dotnet run --project src/Luthn.Tools -- seed-demo
printf '%s' "$LUTHN_SERVICE_VALUE" | dotnet run --project src/Luthn.Tools -- token-digest --stdin
LUTHN_BASE_URL=http://localhost:8080 dotnet run --project src/Luthn.McpServer -- --list-tools
```

커밋 전 로컬 runtime 설정, 개발 에이전트 자료, 비공개 원본, key가 든 설정이 stage되지 않았는지 확인합니다.
