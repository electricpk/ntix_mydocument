
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