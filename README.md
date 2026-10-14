# Shimeji — Unity 3D 데스크톱 마스코트

SD(치비) 3D 모델 파일을 `Characters/` 폴더에 넣으면 리그를 자동 판별하고 스켈레톤 애니메이션을 붙여,
바탕화면 위를 걷고, 앉고, 창 위에 올라서고, 벽을 타고, 천장에 매달리고, 떨어지는 데스크톱 마스코트입니다.

- 대상: Windows 10/11 64bit
- 엔진: Unity 2022.3.49f1 (Built-in RP, Mono)

## 빠른 시작 (빌드 사용)

1. `Builds/Win64/Shimeji.exe`를 실행합니다.
2. 샘플 캐릭터 세 개(`SampleChibi` 3D 휴머노이드, `SampleBlob` 3D 제네릭, `SampleSprite` 2D 이미지)가 화면 위에서 떨어져 내려옵니다.
3. 캐릭터를 바꾸거나 추가하려면 `Builds/Win64/Characters/<이름>/model.vrm`(또는 `.glb`, `.gltf`)을 넣습니다. 재시작 없이 바로 나타납니다.
4. 종료는 작업표시줄 트레이 아이콘 → **종료**로 합니다.

## 빌드 방법

```bash
"C:/Program Files/Unity/Hub/Editor/2022.3.49f1/Editor/Unity.exe" -batchmode -quit -projectPath D:/UnityProjects/Shimeji -executeMethod Shimeji.EditorTools.ShimejiEditorTools.SetupAndBuild -logFile build.log
```

에디터 메뉴로도 할 수 있습니다.
- **Shimeji/Setup Everything**: 프로젝트 설정, 샘플 GLB 생성, 공용 클립 베이크, 씬 생성
- **Shimeji/Build Windows (x64)**: `Builds/Win64/`에 빌드하고 `Characters/`를 복사

에디터 Play 모드에서는 투명 창, 클릭 통과, 실제 창 감지를 확인할 수 없습니다. 대신 Game 뷰를 가짜 데스크톱(모니터 1개 + 가짜 창 2개, 불투명 배경)으로 대신합니다. 실제 동작은 반드시 Windows 빌드로 확인하세요.

## 사용법

| 입력 | 동작 |
|---|---|
| 좌클릭 | 클릭 반응 + 말풍선 |
| 더블클릭 | 춤추기 |
| 드래그 | 집어서 옮기기. 놓으면 떨어지고, 빠르게 놓으면 던져져 날아가서 벽에 붙거나 바닥/창 위에 착지 |
| 우클릭 | 메뉴: 쓰다듬기, 앉기, 자기, 손 흔들기, 춤추기, 크게/작게, 복제(소환), 이 캐릭터 제거, 전체 일시정지 |
| 캐릭터 밖 | 클릭이 아래 창/바탕화면으로 그대로 통과 |

트레이 메뉴에서는 다음을 설정할 수 있습니다: 캐릭터 켜기/끄기, 전체 크기(50~200%), 활동량(느림/보통/활발), 항상 위, 일시정지, 커서 바라보기, 정밀 클릭 판정(알파), 60 FPS, 디버그 표면 표시, 캐릭터 폴더 열기, 캐릭터 다시 읽기, 로그 폴더 열기, Windows 시작 시 실행, 종료.

설정은 `%USERPROFILE%/AppData/LocalLow/ShimejiProject/Shimeji/settings.json`에 저장됩니다. 캐릭터 위치도 함께 저장되어 다음 실행 때 복원됩니다.

## 캐릭터 폴더 규약

```
<실행 파일 폴더>/Characters/
  └─ <캐릭터 이름>/
       ├─ model.vrm | model.glb | model.gltf   (필수, 런타임 로딩)
       ├─ character.json                        (선택, 설정 덮어쓰기)
       └─ animations/*.glb                      (선택, 모델 전용 클립)
```

- **VRM 0.x / 1.0**: UniVRM으로 읽습니다. 휴머노이드 아바타와 VRM 스프링본을 그대로 씁니다.
- **GLB/glTF**: UniGLTF로 읽습니다. 본 이름(Mixamo, Bip001, VRM `J_Bip_*`, Blender `.L/.R` 등)으로 휴머노이드 아바타를 런타임에 만들고, 만들 수 없으면 제네릭(절차 애니메이션)으로 동작합니다.
- **FBX**(에디터 전용): `Assets/Characters/`에 넣으면 휴머노이드로 임포트되고 `Assets/Resources/BuiltinCharacters/`에 프리팹이 만들어져 빌드에 포함됩니다. 같은 이름의 `.json`은 설정으로 쓰입니다.
- 모델 내장 클립이나 `animations/` 클립의 이름이 상태 키와 맞으면 공용 클립보다 우선합니다. 대소문자는 무시하고 접두사는 허용합니다(예: `chibi_wave` → Wave). 일부 키만 있으면 그 키만 바뀌고 나머지는 공용 클립을 씁니다.
- 잘못된 파일은 앱이 죽지 않습니다. 트레이 알림과 `Logs/`에 기록만 남깁니다.

상태 키: `Idle, IdleLookAround, Walk, Run, Sit, SitEdge, Sleep, Jump, Fall, Land, Dragged, Thrown, ClimbWall, HangCeiling, CrawlCeiling, ClickReact, Wave, Dance`

### 2D 이미지 캐릭터 (PNG 한 장)

3D 모델이 없고 이미지만 있는 폴더는 2D 캐릭터로 인식합니다. 파일명은 `image.png` / `sprite.png` / `character.png` 우선이고, 없으면 아무 PNG·JPG나 씁니다(`icon.png` 제외).

```
Characters/MyDrawing/
  ├─ image.png          (투명 배경 필수, 서 있는 정면 또는 반측면 그림)
  ├─ character.json     (관절 설정을 완료하면 자동 생성/갱신)
  └─ skeleton.png       (관절 설정 결과 미리보기, 자동 생성)
```

동작 방식:
1. **실행하면 먼저 관절(앵커) 설정 화면이 뜹니다.** 저장된 관절이 없는 2D 캐릭터마다 한 번씩 뜨고, 실행 중에 새 폴더를 넣어도 뜹니다. 그림이 화면 가운데에 크게 표시되고, 자동 감지한 위치가 미리 찍혀 있습니다.
   - 오른쪽 목록에서 부위를 고르거나(안내 순서: 머리 꼭대기 → 목 → 골반 → 어깨/손 → 다리 시작/발 → 팔꿈치/무릎) 그림을 클릭하면 그 위치로 지정되고 다음 부위로 넘어갑니다.
   - 점은 드래그해서 옮길 수 있습니다. 팔꿈치와 무릎은 따로 지정하지 않으면 양 끝의 중간에 자동으로 놓입니다.
   - 뼈대 선이 실시간으로 그려집니다(빨강 = 화면 왼쪽, 파랑 = 화면 오른쪽, 노랑 = 몸 중심).
   - **완료**를 누르면 관절이 `character.json`의 `joints`에 저장되고, 뼈대를 그린 `skeleton.png`가 같은 폴더에 생긴 뒤 캐릭터가 나타납니다. 다음 실행부터는 이 화면이 뜨지 않습니다.
   - **나중에**를 누르면 이번 실행만 자동 감지 결과로 시작합니다.
   - 다시 설정하려면 캐릭터를 우클릭하고 **관절 다시 설정 (2D)**을 누르세요.
2. **자동 관절 감지**(설정 화면의 초기값): 실루엣에서 폭이 가장 좁은 곳을 목으로, 다리가 갈라지는 곳을 가랑이로, 몸통 밖으로 가장 멀리 나온 점을 손으로 잡습니다.
3. **자동 리깅**: 지정한 관절로 뼈대를 만들고, 격자 메시와 본 가중치를 자동 계산합니다. 팔 본은 몸통 바깥 영역만, 다리 본은 가랑이 아래만 따르게 해서 팔을 움직여도 몸통이 찢어지지 않습니다.
4. **애니메이션**: 3D와 같은 행동/포즈 정의를 2D로 옮깁니다. 팔은 화면 평면에서 회전하고, 다리는 원근 축소로 들어 올리거나 앉으며, 몸통과 머리는 기울어집니다. 진행 방향에 따라 좌우가 반전됩니다.

잘 되는 그림: 투명 배경, 서 있는 자세, 팔이 몸에서 조금 떨어져 있고 두 다리가 보이는 그림. 팔이 몸 앞에 겹쳐 있거나 긴 치마로 다리가 안 보이면 자동 감지가 비율 추정으로 대체됩니다. 이때는 관절 편집으로 고치거나 `spriteLimbAmount`를 낮추세요.

2D 전용 `character.json` 항목:

```json
{
  "spriteFacing": "front",      // front | left | right : 그림이 보는 방향 (좌우 반전 기준)
  "spriteLimbAmount": 1.0,      // 팔다리 움직임 세기 (0 = 거의 통이미지, 2 = 과장)
  "joints": {                   // 관절 위치 (이미지 정규화 좌표 0..1, 좌상단 원점). 관절 설정 화면이 자동으로 써 줌
    "neck": [0.5, 0.42], "handA": [0.12, 0.6]
  }
}
```

관절 이름: `top, neck, hips, shoulderA, elbowA, handA, shoulderB, elbowB, handB, hipA, kneeA, footA, hipB, kneeB, footB` (A = 이미지 왼쪽, B = 이미지 오른쪽)

### character.json (모든 항목 선택)

```json
{
  "displayName": "Chibi A",
  "heightPixels": 160,          // 화면에서의 키 (24~1200)
  "facingYawDeg": 90,           // 오른쪽을 볼 때 모델 Y 회전
  "viewAngleDeg": 20,           // 카메라 쪽으로 살짝 돌리는 3/4 뷰 각도
  "rigType": "auto",            // auto | humanoid | generic
  "boneMapOverride": { "Hips": "Bip001 Pelvis" },
  "clipOverrides": { "Walk": "my_walk" },
  "behaviorProfile": "default", // StreamingAssets/Behaviors/<이름>.json
  "enableSpringBones": true,
  "colliderPreset": "chibi",    // chibi | box
  "colliderScale": 1.0,
  "armSpreadBias": 8,           // SD 비율 보정: 팔을 바깥으로 벌리는 각도
  "lookAtCursor": true
}
```

모델 정면 방향(+Z/-Z/±X)은 좌우 본 위치로 자동 보정합니다.

### 행동 프로필

`Shimeji_Data/StreamingAssets/Behaviors/default.json`을 고치거나 복사해서 캐릭터별 행동을 정의합니다. 각 항목은 가중치, 지속 시간, 속도, 조건(`NearEdge`, `WallAdjacent`, `!BlockedLeft` 등), 다음 행동 후보, 쿨다운을 가집니다. 원조 시메지의 behaviors XML과 같은 역할입니다.

## 구조

```
Assets/Scripts/
  Core/            순수 C# (UnityEngine 비의존): ScreenSpace, DesktopWorld, CharacterBody(물리),
                   BehaviorSelector/CharacterBrain, BoneMapper, ProceduralPoses, 설정, 입력 판정, 로그
  Platform/Win32/  P/Invoke 격리: 레이어드 오버레이 창, 창/모니터 수집, 마우스 폴링, 트레이, 자동 시작, 단일 인스턴스
  Characters/      로더(VRM/GLB), 리그 판별 + 런타임 아바타, 애니메이터(모델 클립 > 공용 클립 > 절차), 스프링본, 액터
  App/             부트스트랩, 렌더 업로드(LayeredPresenter), UI(컨텍스트 메뉴/말풍선/디버그 선), 테스트 훅
  Editor/          샘플 모델 생성, 휴머노이드 클립 베이커, 프로젝트/씬 설정, 빌드, FBX 후처리
Assets/Shaders/    Shimeji/Toon (Standard 대체 경량 셰이더)
Assets/Animations/ 공용 휴머노이드 클립 18개 + ShimejiBase.controller (코드로 생성, LICENSES.md 참고)
Assets/Tests/      EditMode 테스트 74개
Tools/AcceptanceTests/  Windows 빌드 수용 테스트 스크립트
```

좌표는 "화면 픽셀(좌상단 원점, y 아래로 증가)"이 단일 기준이고, Unity 월드 좌표로의 변환은 `ScreenSpace` 한 곳에서만 합니다. 물리와 행동은 픽셀 공간에서 계산합니다.

## 명세와 다르게 구현한 부분 (이유)

| 명세 | 구현 | 이유 |
|---|---|---|
| Unity 창 + `DwmExtendFrameIntoClientArea`, DXGI flip 끔 | **별도 레이어드 창 + `UpdateLayeredWindowIndirect`**. 카메라 → RenderTexture → 캐릭터 주변 영역만 GPU 비동기 읽기 → 프리멀티플 알파로 업로드. DWM 방식은 `-overlay-backend dwm`으로 남겨 둠 | 개발 PC(AMD RX 5500 XT, Win11)에서 bitblt 스왑체인 출력이 흰 화면이 되고, 플립 모델은 알파가 무시됨. ULW 방식은 드라이버/OS 설정과 무관하고, 알파 0 픽셀은 OS가 클릭을 통과시킴 |
| 오버레이가 가상 데스크톱 전체를 덮음 | 위/아래 1px을 뺀 크기 | 모니터를 완전히 덮는 최상단 창은 셸이 전체화면 앱으로 판정함(QUNS_BUSY). 그러면 알림이 억제되고 자기 자신을 전체화면 앱으로 오인함 |
| 전체화면 감지 = `SHQueryUserNotificationState` | D3D 전체화면/프레젠테이션 상태 + "전경 창이 모니터 전체를 덮는가" 직접 검사 | 위와 같은 이유 |
| GLB는 glTFast | UniGLTF (UniVRM에 포함) | glTF 스택을 하나로 유지 |
| glTF 머티리얼 = Standard | `Shimeji/Toon` (Standard 프로퍼티/키워드 호환, 배리언트 8개) | Standard 전체 배리언트를 포함하면 첫 사용 시 메모리 +80MB |
| 행동 = ScriptableObject | JSON (`StreamingAssets/Behaviors`) | Core를 엔진 비의존으로 유지하고, 빌드 후에도 수정 가능 |
| 컨텍스트 메뉴 = uGUI | uGUI (Screen Space - Camera), EventSystem 대신 Win32 폴링 좌표로 직접 히트테스트 | 클릭 통과 창에서는 Unity 입력이 오지 않음 |
| 스크립팅 백엔드 Mono/IL2CPP 둘 다 확인 | Mono만 확인 | 이 PC에 IL2CPP 모듈이 설치되어 있지 않음. P/Invoke 콜백에는 `MonoPInvokeCallback`을 붙여 둠 |

## 테스트 결과

- **EditMode 74개 전부 통과** (2D: 관절 감지/폴백/덮어쓰기, 메시/가중치 규칙, 실제 PNG 로딩·포즈, 불투명 이미지 거부 포함): 좌표 왕복(음수 좌표 모니터), 표면 생성/창 필터/가림 처리, 물리(착지, 창 추종, 최소화 시 낙하, 벽 타기 → 창 위/천장, 던지기, 복구), 행동 선택(가중치, 조건, 쿨다운, 고정 시드), 본 매핑(Mixamo/Bip001/VRM/Blender/임의 이름), JSON 파싱, 입력 판정, 실제 GLB 로딩(휴머노이드/제네릭/모델 클립 우선/깨진 파일), 폴더 감시, Win32 스모크, **20분 방치 시뮬레이션**(창 이동/최소화/Z순서 변경/던지기 포함, 시드 3개)
- **Windows 빌드 수용 테스트** (`Tools/AcceptanceTests/acceptance.ps1`): 25개 항목 모두 통과한 실행이 있음. 다만 마우스 메뉴 항목 테스트는 테스트 스크립트의 대기 시간 문제로 실행에 따라 실패한 적이 있음(앱 로그상 클릭은 정상 처리됨)
  - 성능(캐릭터 1개, 4코어 이상): CPU 약 0.6~1.2%, 작업 집합 약 220~260MB
  - 잔상 검사: 렌더 결과와 실제 창 내용을 비교해 잔상 픽셀 0

수용 테스트 스크립트는 약 1분 동안 마우스를 직접 움직입니다(캐릭터/테스트 창 위에서만 클릭). 실행 방법:

```bash
powershell -ExecutionPolicy Bypass -File Tools/AcceptanceTests/acceptance.ps1
```

### 수동 체크리스트
- 멀티 모니터(주 모니터가 가운데/왼쪽/오른쪽), 배율 100%/150% — 개발 PC는 단일 모니터라 직접 확인하지 못했음
- 가상 데스크톱 전환, 실제 게임/영상 전체화면
- 24시간 구동 시 메모리 누수 여부
- 실제 VRM 파일 (샘플은 GLB만 포함)

## 문제 해결

- **캐릭터가 안 보임**: `Logs/` 최신 로그에서 `overlay backend`와 로드 오류를 확인하세요. `-overlay-backend dwm`으로 실행해 비교할 수 있습니다.
- **어떤 창 위에 안 올라감**: 다른 창에 가려진 윗면, 최대화 창, 툴윈도우, 클로킹된 창은 표면에서 제외됩니다. 트레이 → 디버그: 표면 표시로 실제 표면을 볼 수 있습니다(초록=바닥, 하늘=창 윗면, 노랑=벽, 분홍=천장).
- **모델이 누워 있거나 뒤를 봄**: `character.json`의 `facingYawDeg`를 조정하세요. 본 인식이 틀리면 `boneMapOverride`를 쓰세요.
- **필수 본(Hips/Head/양발)을 찾지 못함**: 로그에 사유가 남습니다. `boneMapOverride`로 지정하세요.
- 진단용 실행 인자: `-shimeji-test`(Logs/status.json, commands.txt), `-overlay-backend dwm`, `-overlay-mode N`, `-overlay-rect x,y,w,h`

## 사용한 패키지 / 에셋

- **UniVRM v0.131.3** (`com.vrmc.gltf`, `com.vrmc.vrm`, MIT): VRM/glTF 런타임 로딩, VRM 스프링본, 샘플 GLB 내보내기
- Unity 내장 패키지: uGUI(메뉴/말풍선), Test Framework(EditMode 테스트), Timeline(UniVRM 의존성)
- 애니메이션 클립과 샘플 모델은 모두 이 프로젝트에서 코드로 생성했습니다(서드파티 모션/모델 없음). `Assets/Animations/Library/LICENSES.md` 참고
