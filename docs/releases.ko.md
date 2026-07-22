# 컨테이너 릴리즈

[English](releases.md)

Luthn에서 버전을 관리하는 릴리즈 산출물은 컨테이너 하나입니다. PR이 `main`에
머지되면 개발용 `main`과 `sha-<commit>` 이미지는 자동 발행되지만 정식 릴리즈는
생성되지 않습니다.

정식 릴리즈는 대표님의 명시적 요청이 있을 때만 실행합니다. 사람, Codex, Claude
Code와 다른 agent는 모두 같은 저장소 공용 entrypoint를 사용합니다.

```bash
python3 scripts/release-container.py v0.1.0 --dry-run
python3 scripts/release-container.py v0.1.0
```

entrypoint는 로컬 작업트리를 읽거나 변경하지 않고 원격 `main`의 현재 commit을
확정합니다. 엄격한 버전 형식이 아니거나, 대상 commit이 원격 `main`의 정확한
최신 commit이 아니거나, 같은 릴리즈 tag가 이미 있으면 중단합니다. 유효하면
버전과 고정 source commit으로 `Release container` GitHub workflow를 호출합니다.

최종 규칙은 workflow가 강제합니다.

| 참조 | 의미 |
| --- | --- |
| `main` | 일반 `main` 머지마다 바뀌는 최신 개발 이미지 |
| `sha-<commit>` | 개발·진단용 정확한 source revision |
| `vMAJOR.MINOR.PATCH` | 대표님이 선택한 변경 불가 정식 릴리즈 |
| `vMAJOR.MINOR` | 해당 release line의 최신 patch |
| `stable` | 일반 설치·업데이트 채널 |
| `@sha256:<digest>` | 실제 실행·rollback 식별자 |

workflow는 고정 source commit을 빌드하고 모든 platform에 SemVer와 source
revision label을 넣으며, multi-architecture manifest와 익명 pull을 검증한 뒤
변경 불가 Git `vMAJOR.MINOR.PATCH` tag를 생성합니다. 별도 package, binary asset,
GitHub Release 페이지는 필요하지 않습니다.

## 설치와 업데이트

일반 설치는 `stable`을 추적하지만 실제 실행 이미지는 해석된 digest로 저장합니다.

```bash
curl -fsSL https://raw.githubusercontent.com/JakobSung/Luthn/main/scripts/install.sh \
  | bash -s -- --channel stable
```

자동 채널 업데이트가 필요 없으면 특정 릴리즈를 선택합니다.

```bash
curl -fsSL https://raw.githubusercontent.com/JakobSung/Luthn/main/scripts/install.sh \
  | bash -s -- --version v0.1.0
```

`luthn version --json`은 update channel과 변경 불가 설치 image·실행 digest를
분리해 보고합니다. `luthn update check`는 pull이나 상태 변경 없이 공식 mutable
channel만 확인합니다. `luthn update`는 대상 channel을 digest로 확정하고 database
backup, migration, 재시작과 readiness 검증을 수행합니다. SemVer로 설치한 상태는
운영자가 `stable`, `main` 또는 다른 release를 명시적으로 선택할 때까지 고정됩니다.

이 절차를 agent별 지시 파일에 복사하지 않습니다. agent 지시는 이 문서와 명시적
소유자 요청 조건만 가리키고, 공용 entrypoint와 GitHub workflow를 최종 기준으로
사용합니다.
