# 운영

[English](operations.md)

직접 호스팅 schema migration, backup·복원, 검색 도입 결정을 설명합니다. 환경별 비밀 값, 비공개 원본, 고객 원문, 일회성 실행 증거는 기록하지 않습니다.

## Docker 배포 수명주기

```bash
luthn status
luthn update
luthn reset --yes
luthn uninstall
```

원본 없는 배포는 고정 Compose project `luthn`, volume `luthn-postgres`·`luthn-operator`, `~/.config/luthn` 설정, `~/.local/share/luthn` runtime, `~/.local/state/luthn` 갱신·backup을 사용합니다. 실행 폴더가 바뀌어도 다른 project나 빈 volume을 선택하면 안 됩니다.

`update`는 현재 image id를 기록하고 대상 image를 받아 runtime을 갱신한 뒤 API와 쓰기 경로를 멈추고 PostgreSQL backup, migration, API 시작, health/readiness 확인을 수행합니다. install·status·update 중 `docker compose down -v`를 사용하지 않습니다. volume 삭제는 `reset --yes` 또는 `uninstall --purge-data --yes`에서만 합니다.

## 직접 호스팅 Migration 모형

Schema 변경은 API 시작의 부수 효과가 아니라 명시적 유지 관리 단계입니다. 적용 전에 image와 migration 집합을 맞추고 migration script를 검토하며 저장소 밖에 backup을 만들고 임시 database로 복원 검증을 합니다. token·connection string은 secret 저장소나 runtime 환경에만 둡니다.

### Backup과 적용

```bash
docker compose --project-name luthn \
  --env-file "$HOME/.config/luthn/luthn.env" \
  -f "$HOME/.local/share/luthn/compose.yaml" \
  exec -T postgres pg_dump -U luthn -d luthn -Fc > luthn.backup

luthn update
curl http://localhost:8080/readyz
```

`/readyz`가 `ready`여야 traffic을 받을 수 있습니다. `/healthz`만으로 schema 준비를 보장하지 않습니다.

### 복원과 되돌리기

API와 MCP/adapter 쓰기 경로를 멈춘 뒤 복원하고 migration을 실행한 다음 `/readyz`를 확인합니다. `~/.local/state/luthn/install-state.env`의 `PREVIOUS_IMAGE_ID`, `BACKUP_PATH`, 이전 image, migration 직전 backup을 보존합니다. 실패해도 database를 자동 복원하거나 내리지 않습니다. 이전 API가 새 schema와 호환된다고 가정할 수 없기 때문입니다.

감사/control event와 worker 재시도·dead-letter에는 메타데이터만 둡니다. 원본 payload 열, Vault 원본 조회, 승인·감사 경계를 우회하는 connector/MCP tool을 추가하지 않습니다.

## 검색 도입 모형

기본 검색은 공개·에이전트 허용 위키 기록에 대한 결정적 process 내부 검색입니다. 모든 retrieval backend는 `public-agent-allowed-safe-projections` 경계를 공유합니다. PostgreSQL `pgvector`는 첫 vector 후보지만 원본 Vault/source를 embedding·index·log·반환하면 안 됩니다.

| 선택지 | 적합성 | 운영 비용 | 안전 경계 |
|---|---|---|---|
| 결정적 process 내부 검색 | 현재 기본, 가장 작은 구성 | 추가 service 없음 | 공개·에이전트 허용 기록만 검색 |
| PostgreSQL `pgvector` | PostgreSQL 단일 data plane을 원할 때 첫 vector 선택 | extension, embedding, index, migration 필요 | 공개 안전 투영만 embedding 저장 |
| 외부 검색 engine | UX·혼합 순위·오타·facet·규모가 기존 방식을 넘을 때 | 별도 service, backup, 인증, index pipeline | 원본/Vault index 금지 |

교체 전 검색 corpus를 위키 안전 투영으로 한정하고, 원본이 embedding/index/log/응답에 없음을 증명하며, index backup·복원을 기록하고, `id`, `title`, `safeSummary`, `sensitivity`, `coreTags`, `score` 호환성과 기밀 기록 제외 시험을 추가해야 합니다.

## 외부 기억 서비스 Adapter 경계

외부 기억 서비스는 선택적 adapter이며 두 번째 원본 저장 경로가 아닙니다. `metadata-only`, `safe-projection-only`인 `public-agent-allowed-safe-projections`만 내보냅니다. 항목은 공개이고 `PublicSafe` 또는 `SharedAcrossAgents`로 보이며 만료되지 않아야 합니다. 별도 안전 분류 경로가 생길 때까지 `title`과 Core tag는 비워 둡니다.

### 로컬 Outbox 동작

기존 기억은 `LocalOnly`, revision `1`로 채우며 과거 자료를 queue에 넣지 않습니다. 새 revision이 있으면 전송 전 이전 작업은 `Superseded`가 됩니다.

```bash
docker compose --env-file .env --profile sync-worker up -d worker
```

기본 stack은 worker를 시작하지 않습니다. 시작해도 공개 build는 비활성 transport만 등록해 외부 연결을 하지 않고 pending outbox를 그대로 둡니다. 실제 cloud adapter는 endpoint 인증, tenant 격리, 삭제, backup/복원, 감사 경계를 별도로 검토한 뒤에만 배포합니다.
