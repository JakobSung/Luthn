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

운영자 화면은 <http://127.0.0.1:5089/>입니다. health/readiness, 읽기 전용 에이전트 연결 상태, 분류 미리 보기, 통제된 source intake, 민감 접근 요청 검토·승인·거절, 목적 중심 메타데이터 감사 조사를 제공합니다. 민감 요청은 먼저 선택해 허용된 operator detail만 조회해야 하며 명시적 결정 사유 없이는 승인·반려할 수 없습니다. 감사 센터는 민감 접근, 분류 실패, 설정 변경, publication, ingress, worker, retention preset과 제한된 사용자 필터를 제공하지만 원문 조회 화면이 아닙니다.

선택 활성화 OSS Hub 기준선은 기본 비활성입니다. 로컬에서 시험할 때는 server-bound Hub scope를 가진 `MultiUser` identity를 사용한 뒤 `Luthn__Hub__Ingress__Enabled=true`와 필요하면 `Luthn__Hub__Ingress__WorkerEnabled=true`를 설정합니다. Ingress는 제한된 capsule을 암호화하고 trusted token에서 organization/workspace/member/agent/session identity를 정하며 metadata receipt만 반환합니다.

Cloud client transport는 다음 값을 모두 명시하기 전까지 비활성입니다.

```dotenv
Luthn__Cloud__Enabled=true
Luthn__Cloud__BaseUrl=https://your-cloud-origin.example/
Luthn__Cloud__Audience=luthn-cloud
Luthn__Cloud__StateDirectory=.luthn/operator
Luthn__Console__Enrollment__Adapter=Cloud
```

API와 Worker는 같은 상태 directory와 Data Protection key ring을 사용해야 합니다.
명시적인 loopback 시험 origin 외에는 HTTPS만 사용합니다. 현재 M3 client는 enrollment와
safe projection sync를 수행하지만 사람의 Cloud login은 M4 경계이므로 activation 후
`CloudLoginRequired` 상태가 됩니다. 제한값과 Cloud 경계는
[중앙 팀 Hub data plane](cloud-hub-data-plane.ko.md)을 참고하세요.

에이전트 설치·재설정·연결 해제는 host CLI에서 수행합니다.

## 운영자 콘솔 사용법

먼저 `콘솔 접근` 탭에서 세션 상태를 확인한 뒤 작업 메뉴를 선택합니다. 개발·패키지형
개인 설치는 `Luthn__Console__LocalOnly=true`를 명시하고 공개 port를 `127.0.0.1`에
바인딩합니다. 미등록 `SingleOwner`에서는 `luthn console`이 권한 없는 HttpOnly 브라우저
후보 하나만 승인한 뒤 제한된 서버측 LocalAuto 세션을 발급합니다. 자격이나 bootstrap
값을 URL·API 본문에 전달하지 않으며 URL 직접 접속만으로는 발급하지 않습니다.
브라우저는 service/decision bearer 값을 읽거나 저장하거나 전송하지 않으며, cookie 인증
변경 요청에는 Host가 반환한 same-origin CSRF header가 필요합니다.

원본 기반 self-host 설치는 Git에서 무시되며 권한이 제한된 `.env`에
`LUTHN_SERVICE_VALUE`와 `LUTHN_OPERATOR_VALUE`를 만듭니다. source 설치의 operator
token은 기본적으로 결정 전용입니다. 패키지 설치는 같은 secret을
`~/.config/luthn/service-token`, `~/.config/luthn/operator-token`에 보관합니다(Windows는
`%LOCALAPPDATA%\\Luthn\\config\\service-token`, `operator-token`). 이 파일을 출력하거나
커밋하지 않습니다.

이 자격 증명은 Agent와 직접 API client에는 계속 필요하지만 사람의 콘솔 세션으로
승격되지 않습니다. 수명주기 시험은 `Luthn:Console:Enrollment:Adapter=Fake`와
`Luthn:Console:CloudLogin:Provider=Fake`를 사용할 수 있습니다. 둘 다 기본값은
`Disabled`입니다. Fake adapter는 네트워크 요청을 하지 않습니다. `Cloud` enrollment
adapter는 실제 client이므로 위의 보호된 Cloud client 설정을 명시적으로 활성화했을 때만
사용합니다. Fake recovery verifier도 집중 시험에서 명시적으로 켜지 않으면 비활성입니다.

메뉴는 작업별로 사용합니다.

- **개요**: 배포 경계, health/readiness, connector 상태를 확인합니다.
- **민감 접근 승인**: 제한된 operator detail을 확인한 뒤 명시적인 사유로 승인·반려합니다.
  Vault/source 원문은 표시하지 않습니다.
- **외부 공개**: 민감 접근과 분리된 외부 공개 결정 경로입니다.
- **분류·수집**: 분류 미리 보기와 안전한 source intake를 수행합니다.
- **감사 센터**: preset·filter·cursor pagination·metadata-only export로 이벤트를 조사합니다.

직접 bearer client에서 `403`이 나오면 서버 설정의 해당 token에 필요한 scope를
추가하세요. 권한 오류를 해결하려고 agent connector에 더 넓은 token을
넣지 않습니다.

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

`X-Luthn-Operator`는 감사 actor를 구분하는 선택적 메타데이터이며 권한을 주지 않습니다. 지원 scope는 `agent.read`, `agent.write.summary`, `agent.connection.read`, `agent.connection.write`, `classification.preview`, `config.write`, `external-publication.read`, `external-publication.write`, `source.write`, `memory.read`, `memory.write`, `access.request`, `access.review`, `access.decide`, `audit.read`, `metrics.read`, `metrics.write`, 운영자용 `*`입니다.

새 설치의 기본 identity 경계는 기존과 호환되는 단일 owner입니다.

```bash
Luthn__Identity__Mode=SingleOwner
Luthn__Identity__SingleOwnerUserId=local-owner
Luthn__Auth__Tokens__0__UserId=local-owner
Luthn__Auth__Tokens__0__WorkspaceId=default
Luthn__Auth__Tokens__0__ActorKind=Agent
Luthn__Auth__Tokens__0__IsOperator=false
```

로컬 multi-user 배포는 mode를 바꾸고 모든 비운영자 product token에 하나의 제한된 user
ID를 연결합니다. ID는 소문자로 정규화하며 첫 글자는 영문자·숫자여야 하고 전체 길이는
128자 이하입니다. 허용 문자는 영문자, 숫자, `.`, `_`, `:`, `@`, `-`입니다. binding이
없거나 잘못되면 `503`을 반환하며 caller JSON으로 덮어쓸 수 없습니다.

```bash
Luthn__Identity__Mode=MultiUser
Luthn__Auth__Tokens__0__UserId=alice
Luthn__Auth__Tokens__0__WorkspaceId=team-alpha
Luthn__Auth__Tokens__0__ActorKind=Agent
Luthn__Auth__Tokens__0__IsOperator=false
Luthn__Auth__Tokens__1__Name=local-operator
Luthn__Auth__Tokens__1__UserId=operator
Luthn__Auth__Tokens__1__WorkspaceId=team-alpha
Luthn__Auth__Tokens__1__ActorKind=Service
Luthn__Auth__Tokens__1__IsOperator=true
```

user 또는 connector마다 별도 최소권한 token을 사용합니다. 같은 팀에서 공유할 token은
같은 `WorkspaceId`에 연결하고, 다른 workspace token과 데이터가 섞이지 않게 합니다.
`IsOperator=true`도 product data의 workspace 경계를 우회하지 않습니다.
`X-Luthn-Operator` header는 계속 audit metadata일 뿐 역할을 부여하지 않습니다. identity
설정 변경 뒤 `/readyz`를 확인합니다.

## 분류 Provider 설정

운영자 화면의 `/api/operator/classification-provider`에서 `LocalDeterministic` 또는 선택적 `LocalHttp`를 설정하고 연결 시험을 실행할 수 있습니다. 상용 provider, credential, model, 인증 header는 지원하지 않습니다. `LocalHttp`는 `localhost`, IPv4·IPv6 loopback, `host.docker.internal`의 절대 HTTP(S) endpoint만 허용하며 redirect는 실패 처리합니다.

```bash
Luthn__Classification__Provider=LocalDeterministic
Luthn__Classification__Runtime__TimeoutSeconds=30
Luthn__Classification__Runtime__MaxAttempts=2
Luthn__Classification__Runtime__RetryDelayMilliseconds=200
```

일시적 timeout, HTTP 408/429/5xx만 재시도합니다. 측정값은 `luthn.classification_provider.attempts`, `retries`, `failures`, `luthn.safe_search.candidates`입니다.

### 분류 Golden 평가

버전이 지정된 한국어 중심 합성 corpus를 network 요청 없이 `LocalDeterministic`으로
평가합니다.

```bash
dotnet run --project src/Luthn.Tools -- classification-eval
```

같은 안정된 JSON 결과를 파일로 남길 수 있습니다.

```bash
dotnet run --project src/Luthn.Tools -- classification-eval \
  --output artifacts/classification-eval.json
```

network 요청 없이 로컬 기준값과 결정론적 guard를 결합한 경로도 평가할 수
있습니다.

```bash
dotnet run --project src/Luthn.Tools -- classification-eval \
  --provider guarded-local
```

동일 장비 Host API를 평가하려면 허용된 로컬 URL에서 API를 실행합니다. 보호 API
token 값은 command line에 넣지 말고 환경 변수 이름만 전달합니다.

```bash
export LUTHN_EVAL_TOKEN='<운영자가 제공한 token>'
dotnet run --project src/Luthn.Tools -- classification-eval \
  --provider local-http \
  --api-url http://127.0.0.1:5089 \
  --token-env LUTHN_EVAL_TOKEN
```

결과에는 corpus 원문을 넣지 않고 제한된 case ID, case별 분류·저장 경로 비교,
불일치 합계만 기록합니다.

runtime은 모든 `LocalHttp` 결과에 로컬 결정론적 guard 버전 `1`을 결합하며,
provider 실패를 detector 단독 저장으로 대체하지 않습니다.

```bash
Luthn__Classification__Provider=LocalHttp
Luthn__Classification__LocalHttp__Endpoint=http://host.docker.internal:11434/classify
Luthn__OperatorConfig__Directory=/var/lib/luthn/operator
```

기존 상용 provider, `Mock`, `ExternalHttp`, 원격 `LocalHttp` 설정은 secret을 복호화하거나 사용하지 않고 endpoint·model·인증·credential을 비운 `Unconfigured`로 전환합니다. 응답은 `sensitivity`, `confidence`, `categories`, `containsSensitiveMaterial`을 반환해야 합니다.

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
