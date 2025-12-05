// #define MOVE_VALIDATOR_DEBUG
// #define CURVE_MOVE_VALIDATOR_DEBUG

using UnityEngine;
using System;
using System.Linq;
using System.Collections.Generic;
using UniRx;
using Mirror;
using FrameWork.Util;

public partial class Character
{
#if UNITY_EDITOR || DEVELOPMENT_BUILD
    private static bool _hasLoggedOverrideWarning = false;
#endif


    protected MoveValidator m_moveValidator = new();
    protected CurveMoveValidator m_curveMoveValidator = new();

    [ClientRpc]
    public void RpcSetPositionForce(Vector3 position)
    {
        SetPosition(position);
    }

    /// <summary>
    /// 각 캐릭터별 액션 커브 반환. 
    /// ⚠️ 자식 클래스에서 override하여 캐릭터 액션별 커브를 반환하세요. 이동 검증시 필요
    /// </summary>
    /// <param name="actionInfo">액션 정보</param>
    /// <param name="isOverride">override 하고 부모메서드를 호출할 경우 true로 호출하면 경고가 뜨지 않음</param>
    /// <returns></returns>
    [Server]
    protected virtual AnimationCurveAsset GetCurveAssetForValidate(AnimatorStateInfo stateInfo, bool isOverride = false)
    {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
        if (!isOverride && !_hasLoggedOverrideWarning)
        {
            Debug.LogWarning($"🚨 [{GetType().Name}] GetCurveAssetForValidate가 override되지 않았습니다!\n" +
                           $"캐릭터별 커스텀 커브 이동이 제대로 작동하지 않을 수 있습니다.\n" +
                           $"이 경고는 한 번만 표시됩니다.");
            _hasLoggedOverrideWarning = true;
        }
#endif
        
        if (stateInfo.shortNameHash == GetAnimationHash((int)eCharacterAnimState.AirBorne))
        {
            return m_airborneCurve;
        }

        return null;
    }

    /// <summary>
    /// 이동 검증을 위한 클래스
    /// </summary>
    public class MoveValidator
    {
        public struct SnapshotData
        {
            public double time;
            public float speed;
            public Vector3 position;

            public SnapshotData(Character character)
            {
                time = NetworkTime.time;
                speed = character.m_speed.Value;
                position = character.transform.position;
            }
        }

        private Character _character;
        private ReactiveProperty<bool> _isIdleOrMove = new();
        private List<SnapshotData> _snapshotList = new();


        public void Init(Character character)
        {
            _character = character;

            _isIdleOrMove
                .Where(_ => _character != null && _character.isServer)
                .Subscribe(OnValueChanged)
                .AddTo(character);
        }

        /// <summary>
        /// 이동 검증을 위한 스냅샷 추가
        /// </summary>
        public void AddSnapshotForMoveValidation()
        {
            if (_isIdleOrMove.Value)
            {
                _snapshotList.Add(new SnapshotData(_character));
            }
        }

        public void Update()
        {
            if (_character == null || !_character.isServer)
                return;

            _isIdleOrMove.Value = 
                (_character.m_currentAction == eActionState.Idle || _character.m_currentAction == eActionState.Move) &&
                (_character.IsPlaying(eActionState.Idle) || _character.IsPlaying(eActionState.Move));
        }

        private void OnValueChanged(bool value)
        {
            if (value)
            {
                OnStartCheckValid();
            }
            else
            {
                OnEndCheckValid();
            }
        }

        private void OnStartCheckValid()
        {
            _snapshotList.Clear();
            _snapshotList.Add(new SnapshotData(_character));
        }

        private void OnEndCheckValid()
        {
            if (_snapshotList.Count > 0)
            {
                _snapshotList.Add(new SnapshotData(_character));
                ValidateMove(_snapshotList);
            }
        }

        private void ValidateMove(List<SnapshotData> snapshotList)
        {
            return; // hkpark 추후 정상화 예정

            if (_character == null || !_character.isServer || !IsValidSnapshotData(snapshotList))
            {
                return;
            }

            var firstSnapshot = snapshotList.First();
            var lastSnapshot = snapshotList.Last();
            float movedDistance = Vector3.Distance(firstSnapshot.position, lastSnapshot.position);
            float maxDistance = (GetMaxDistance(snapshotList) * 1.05f) + 1.5f; // 미세 오차 허용
            
            // 상세 검증 로그 출력
#if MOVE_VALIDATOR_DEBUG
            LogValidationDetails(snapshotList, movedDistance, maxDistance);
#endif
            
            if (movedDistance > maxDistance)
            {
                Debug.LogError($"[MoveValidator][userSeq:{_character.m_userSeq}] Failed!!!");
                LogValidationDetails(snapshotList, movedDistance, maxDistance);

                _character.StartActionFromServer(new ActionInfo() { state = eActionState.FixedIdle, fromAction = eFromAction.Server });
                _character.SetPosition(firstSnapshot.position);
                _character.RpcSetPositionForce(firstSnapshot.position);
            }
        }

        private void LogValidationDetails(List<SnapshotData> snapshotList, float movedDistance, float maxDistance)
        {
            if (snapshotList == null || snapshotList.Count < 2)
                return;

            var firstSnapshot = snapshotList.First();
            var lastSnapshot = snapshotList.Last();
            
            // 총 시간 계산
            float totalTime = (float)(lastSnapshot.time - firstSnapshot.time);
            
            // 평균 속도 계산
            float averageSpeed = snapshotList.Average(s => s.speed);
            
            // 최대 속도와 최소 속도
            float maxSpeed = snapshotList.Max(s => s.speed);
            float minSpeed = snapshotList.Min(s => s.speed);
            
            // 실제 평균 속도 (거리/시간)
            float actualAverageSpeed = totalTime > 0 ? movedDistance / totalTime : 0f;
            
            // 구간별 상세 정보 (3개 이상일 때만)
            string segmentDetails = "";
            if (snapshotList.Count > 2)
            {
                segmentDetails = "\n[구간별 상세]";
                for (int i = 0; i < snapshotList.Count - 1; i++)
                {
                    var current = snapshotList[i];
                    var next = snapshotList[i + 1];
                    float segmentTime = (float)(next.time - current.time);
                    float segmentDistance = Vector3.Distance(current.position, next.position);
                    float segmentSpeed = segmentTime > 0 ? segmentDistance / segmentTime : 0f;
                    
                    segmentDetails += $"\n  구간{i + 1}: 시간({segmentTime:F3}s) 거리({segmentDistance:F2}) 속도({segmentSpeed:F2}) 설정속도({current.speed:F2})";
                }
            }
            
            // 검증 결과
            bool isValid = movedDistance <= maxDistance;
            string validationResult = isValid ? "통과" : "실패";
            
            Debug.Log($"[MoveValidator 상세][userSeq:{_character.m_userSeq}] 검증결과: {validationResult}\n" +
                     $"스냅샷 개수: {snapshotList.Count}개\n" +
                     $"총 시간: {totalTime:F3}초\n" +
                     $"총 이동거리: {movedDistance:F2}\n" +
                     $"최대 허용거리: {maxDistance:F2}\n" +
                     $"속도 정보 - 평균:{averageSpeed:F2} 최대:{maxSpeed:F2} 최소:{minSpeed:F2}\n" +
                     $"실제 평균속도: {actualAverageSpeed:F2}\n" +
                     $"시작위치: {firstSnapshot.position}\n" +
                     $"종료위치: {lastSnapshot.position}" +
                     segmentDetails);
        }

        private float GetMaxDistance(List<SnapshotData> snapshotList)
        {
            if (snapshotList == null || snapshotList.Count < 2)
                return float.MaxValue;

            float totalMaxDistance = 0f;
            
            // 각 구간별로 최대 이동 가능 거리를 계산
            for (int i = 0; i < snapshotList.Count - 1; i++)
            {
                var currentSnapshot = snapshotList[i];
                var nextSnapshot = snapshotList[i + 1];
                
                // 시간 차이 계산
                float deltaTime = (float)(nextSnapshot.time - currentSnapshot.time);
                
                // 해당 구간에서의 속도로 최대 이동 가능 거리 계산
                // MoveByDirection에서 displacement = speed * Time.deltaTime * direction 사용
                float maxDistanceInSegment = currentSnapshot.speed * deltaTime;
                
                totalMaxDistance += maxDistanceInSegment;
            }
            
            return totalMaxDistance;
        }

        private bool IsValidSnapshotData(List<SnapshotData> snapshotList)
        {
            bool hasSnapshot = snapshotList != null && snapshotList.Count > 1;
            if (!hasSnapshot)
            {
                return false;
            }

            // 너무 짧은 이동 거리는 제외
            var firstSnapshot = snapshotList.First();
            var lastSnapshot = snapshotList.Last();
            float movedDistance = Vector3.Distance(firstSnapshot.position, lastSnapshot.position);
            if (movedDistance < 0.1f)
            {
                return false;
            }

            return true;
        }
    }


    /// <summary>
    /// 커브 이동 검증을 위한 클래스
    /// </summary>
    public class CurveMoveValidator
    {
        public struct SnapshotData
        {
            public double startTime;
            public Vector3 startPosition;
            public AnimationCurveAsset curveAsset;
            public AnimatorStateInfo stateInfo;
            public float startNormalizedTime; // 애니메이션 시작 시점의 normalizedTime
            public Vector3 curveStartOffset; // 커브에서 시작 지점의 오프셋

            public SnapshotData(Character character, AnimatorStateInfo stateInfo, AnimationCurveAsset curveAsset)
            {
                startTime = NetworkTime.time;
                startPosition = character.transform.position;
                this.curveAsset = curveAsset;
                this.stateInfo = stateInfo;
                
                // 네트워크 동기화로 인한 애니메이션 중간 시작 고려
                startNormalizedTime = stateInfo.normalizedTime % 1.0f; // 루프 애니메이션 고려
                
                // 애니메이션 시작 시점에서의 커브 오프셋 계산
                float animationLength = curveAsset.GetCurves().Max(c => c.length);
                float curveTime = startNormalizedTime * animationLength;
                curveStartOffset = curveAsset.GetVector3ByCurves(curveTime, AnimationCurveAsset.Axis.All);
            }
        }

        private Character _character;
        private Dictionary<int, SnapshotData> _activeAnimations = new Dictionary<int, SnapshotData>();

        public void Init(Character character)
        {
            _character = character;

            // 애니메이션 시작 이벤트 구독
            _character.OnAnimationEnterEvent.AsObservable()
                .Where(stateInfo => _character != null && _character.isServer)
                .Subscribe(OnAnimationEnter)
                .AddTo(character);

            // 애니메이션 종료 이벤트 구독
            _character.OnAnimationExitEvent.AsObservable()
                .Where(stateInfo => _character != null && _character.isServer)
                .Subscribe(OnAnimationExit)
                .AddTo(character);
        }

        private void OnAnimationEnter(AnimatorStateInfo stateInfo)
        {
            // 해당 애니메이션에 대한 커브 에셋 가져오기
            var curveAsset = _character.GetCurveAssetForValidate(stateInfo);
            if (curveAsset == null)
                return;

            // 스냅샷 데이터 생성 및 저장
            var snapshotData = new SnapshotData(_character, stateInfo, curveAsset);
            _activeAnimations.AddOrUpdate(stateInfo.shortNameHash, snapshotData);

#if CURVE_MOVE_VALIDATOR_DEBUG
            Debug.Log($"[CurveMoveValidator][userSeq:{_character.m_userSeq}] Animation started - " +
                     $"Hash:{stateInfo.shortNameHash}, StartPos:{snapshotData.startPosition}, " +
                     $"StartNormalizedTime:{snapshotData.startNormalizedTime:F3}, " +
                     $"CurveStartOffset:{snapshotData.curveStartOffset}");
#endif
        }

        private void OnAnimationExit(AnimatorStateInfo stateInfo)
        {
            // 해당 애니메이션의 스냅샷 데이터 확인
            if (!_activeAnimations.TryGetValue(stateInfo.shortNameHash, out var snapshotData))
                return;

            // 검증 수행
            ValidatePosition(snapshotData, stateInfo);

            // 완료된 애니메이션 데이터 제거
            _activeAnimations.Remove(stateInfo.shortNameHash);
        }

        /// <summary>
        /// 커브 기반으로 예상되는 최종 위치 계산 (네트워크 동기화 고려)
        /// </summary>
        private Vector3 GetExpectedEndPosition(SnapshotData snapshotData)
        {
            float animationLength = snapshotData.curveAsset.GetCurves().Max(c => c.length);
            var relativeEndPos = snapshotData.curveAsset.GetVector3ByCurves(animationLength, AnimationCurveAsset.Axis.All);
            
            // 시작 지점의 오프셋을 빼서 실제 이동량만 계산
            var totalMovement = relativeEndPos - snapshotData.curveStartOffset;
            
            // 애니메이션 시작 시점의 캐릭터 Transform을 기준으로 상대 위치를 월드 위치로 변환
            return snapshotData.startPosition + _character.transform.TransformDirection(totalMovement);
        }

        /// <summary>
        /// 실제 애니메이션 진행 시간에 따른 예상 위치 계산 (네트워크 동기화 고려)
        /// </summary>
        private Vector3 GetExpectedPositionAtTime(SnapshotData snapshotData, double currentTime)
        {
            float elapsedTime = (float)(currentTime - snapshotData.startTime);
            float animationLength = snapshotData.curveAsset.GetCurves().Max(c => c.length);
            
            // 애니메이션 시작 지점부터 현재까지의 진행률 계산
            float progressFromStart = elapsedTime / animationLength;
            float currentNormalizedTime = snapshotData.startNormalizedTime + progressFromStart;
            
            // 루프 애니메이션 고려
            if (snapshotData.stateInfo.loop)
            {
                currentNormalizedTime = currentNormalizedTime % 1.0f;
            }
            else
            {
                currentNormalizedTime = Mathf.Clamp01(currentNormalizedTime);
            }
            
            float currentCurveTime = currentNormalizedTime * animationLength;
            var currentCurvePos = snapshotData.curveAsset.GetVector3ByCurves(currentCurveTime, AnimationCurveAsset.Axis.All);
            
            // 시작 지점부터의 상대적 이동량 계산
            var movementFromStart = currentCurvePos - snapshotData.curveStartOffset;
            
            return snapshotData.startPosition + _character.transform.TransformDirection(movementFromStart);
        }

        /// <summary>
        /// 현재 위치가 예상 위치와 근접한지 검증 (네트워크 환경 고려)
        /// </summary>
        private bool IsValidPosition(SnapshotData snapshotData, Vector3 currentPosition, float toleranceDistance = 1.5f)
        {
            return true;    // hkpark 추후 정상화 예정

            // 두 가지 방식으로 검증: 1) 최종 위치 검증, 2) 현재 시점 위치 검증
            var expectedEndPosition = GetExpectedEndPosition(snapshotData);
            var expectedCurrentPosition = GetExpectedPositionAtTime(snapshotData, NetworkTime.time);
            
            float endDistance = Vector3.Distance(currentPosition, expectedEndPosition);
            float currentDistance = Vector3.Distance(currentPosition, expectedCurrentPosition);
            
            // 두 방식 중 하나라도 통과하면 유효한 것으로 간주 (네트워크 환경 고려)
            bool isValidByEndPosition = endDistance <= toleranceDistance;
            bool isValidByCurrentPosition = currentDistance <= toleranceDistance * 0.8f; // 현재 위치는 더 엄격하게

#if CURVE_MOVE_VALIDATOR_DEBUG
            Debug.Log($"[CurveMoveValidator][userSeq:{_character.m_userSeq}] " +
                     $"EndDistance: {endDistance:F3}, CurrentDistance: {currentDistance:F3}, " +
                     $"ExpectedEnd: {expectedEndPosition}, ExpectedCurrent: {expectedCurrentPosition}, " +
                     $"Current: {currentPosition}, StartPos: {snapshotData.startPosition}, " +
                     $"Tolerance: {toleranceDistance:F3}, ValidByEnd: {isValidByEndPosition}, ValidByCurrent: {isValidByCurrentPosition}");
#endif

            return isValidByEndPosition || isValidByCurrentPosition;
        }

        /// <summary>
        /// 애니메이션 종료 시 위치 검증 수행
        /// </summary>
        private void ValidatePosition(SnapshotData snapshotData, AnimatorStateInfo currentStateInfo)
        {
            if (_character == null || !_character.isServer)
                return;

            Vector3 currentPosition = _character.transform.position;
            
            // 애니메이션 지속 시간 계산
            double animationDuration = NetworkTime.time - snapshotData.startTime;
            
            // 이동 거리 계산 (다른 로직에서 사용하기 위해 먼저 계산)
            float totalMoveDistance = Vector3.Distance(snapshotData.startPosition, currentPosition);
            
            // 너무 짧은 애니메이션은 검증 제외 (0.1초 미만)
            if (animationDuration < 0.1)
            {
#if CURVE_MOVE_VALIDATOR_DEBUG
                Debug.Log($"[CurveMoveValidator][userSeq:{_character.m_userSeq}] Animation too short ({animationDuration:F3}s), skipping validation");
#endif
                return;
            }

            // 네트워크 동기화로 인한 빠른 재생 감지 및 검증 완화
            bool isFastPlayback = snapshotData.startNormalizedTime > 0.01f || animationDuration < 0.5f;
            if (isFastPlayback)
            {
#if CURVE_MOVE_VALIDATOR_DEBUG
                Debug.Log($"[CurveMoveValidator][userSeq:{_character.m_userSeq}] Fast playback detected - startNormalizedTime: {snapshotData.startNormalizedTime:F3}, duration: {animationDuration:F3}s");
#endif
                // 빠른 재생 환경에서는 검증을 더 관대하게 처리하거나 생략
                if (totalMoveDistance < 1.0f) // 짧은 이동 거리면 검증 생략
                {
#if CURVE_MOVE_VALIDATOR_DEBUG
                    Debug.Log($"[CurveMoveValidator][userSeq:{_character.m_userSeq}] Skipping validation for fast playback with short movement");
#endif
                    return;
                }
            }

            // 이동 거리가 너무 짧으면 검증 제외
            if (totalMoveDistance < 0.1f)
            {
#if CURVE_MOVE_VALIDATOR_DEBUG
                Debug.Log($"[CurveMoveValidator][userSeq:{_character.m_userSeq}] Move distance too short ({totalMoveDistance:F3}), skipping validation");
#endif
                return;
            }

            // 허용 오차 계산 (네트워크 환경과 애니메이션 특성에 따라 동적 조정)
            float baseTolerance = 2.5f; // 네트워크 환경을 고려하여 기본값 증가
            
            // 네트워크 레이턴시에 따른 추가 허용 오차
            float networkTolerance = 0f;
            if (snapshotData.startNormalizedTime > 0.001f) // 애니메이션이 중간에서 시작된 경우
            {
                networkTolerance = totalMoveDistance * 0.15f; // 15% 추가 허용
            }
            
            // 애니메이션 지속 시간에 따른 허용 오차 (짧은 애니메이션일수록 더 관대하게)
            float durationTolerance = animationDuration < 1.0f ? totalMoveDistance * 0.1f : 0f;
            
            float dynamicTolerance = baseTolerance + networkTolerance + durationTolerance + (totalMoveDistance * 0.05f);
            dynamicTolerance = Mathf.Clamp(dynamicTolerance, 2.0f, 5.0f); // 최소 2m, 최대 5m

            if (!IsValidPosition(snapshotData, currentPosition, dynamicTolerance))
            {
                var expectedPosition = GetExpectedEndPosition(snapshotData);
                float distance = Vector3.Distance(currentPosition, expectedPosition);

                Debug.LogError($"[CurveMoveValidator][userSeq:{_character.m_userSeq}] 🚨 POSITION VALIDATION FAILED! 🚨\n" +
                              $"📊 Animation: {currentStateInfo.shortNameHash}, Duration: {animationDuration:F3}s\n" +
                              $"📍 Expected: {expectedPosition}, Current: {currentPosition}\n" +
                              $"📏 Distance: {distance:F3}, Tolerance: {dynamicTolerance:F3}\n" +
                              $"🏁 Start Position: {snapshotData.startPosition}\n" +
                              $"🌐 Network Details - StartNormalizedTime: {snapshotData.startNormalizedTime:F3}, " +
                              $"CurveStartOffset: {snapshotData.curveStartOffset}\n" +
                              $"⚡ FastPlayback: {isFastPlayback}, TotalMoveDistance: {totalMoveDistance:F3}\n" +
                              $"⚠️  This might indicate a cheat attempt or network desync issue!");

                // 위치 보정 실행
                CorrectPosition(expectedPosition);
            }
            else
            {
#if CURVE_MOVE_VALIDATOR_DEBUG
                Debug.Log($"[CurveMoveValidator][userSeq:{_character.m_userSeq}] Position validation passed for animation {currentStateInfo.shortNameHash}");
#endif
            }
        }

        /// <summary>
        /// 위치 보정 실행
        /// </summary>
        private void CorrectPosition(Vector3 correctedPosition)
        {
            Debug.LogWarning($"[CurveMoveValidator][userSeq:{_character.m_userSeq}] Correcting position to: {correctedPosition}");
            
            // 캐릭터 강제 정지 및 위치 보정
            _character.StartActionFromServer(new ActionInfo() { state = eActionState.FixedIdle, fromAction = eFromAction.Server });
            _character.SetPosition(correctedPosition);
            _character.RpcSetPositionForce(correctedPosition);
        }
    }
}
