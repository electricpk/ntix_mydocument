    /// <summary>
    /// 대쉬 액션 핸들러. Ver1
    /// </summary>
    public class DashHandler : CharacterActionHandler
    {
        public override eActionState ActionState => eActionState.Dash;
        
        // Dash state
        private bool _isRunning;
        private float _elapsed;
        private float _animDuration;
        private float _moveStartTime;
        private float _moveEndTime;
        private float _distance;
        private float _speed;
        private Vector3 _beginPos;
        private Vector3 _dashForward;
        
        /// <summary>
        /// 회피 액션 실행 가능 여부 확인 (기존 IsActionEnable에서 이동)
        /// </summary>
        public override bool CanExecute(Character character, ref ActionInfo actionInfo)
        {
            // 기본 조건 확인
            if (!base.CanExecute(character, ref actionInfo))
                return false;
            
            // 회피 특화 조건들
            
            // 1. 회피 쿨타임 및 MP 확인
            if (!character.IsDashEnable())
                return false;
            
            return true;
        }
        
        /// <summary>
        /// 회피 액션 서버 검증 (DodgeHandler 전용)
        /// </summary>
        public override bool ValidateOnServer(Character character, ref ActionInfo actionInfo)
        {
            // 1. 회피 쿨타임 확인
            if (!character.IsDashEnable())
            {
                Debug.LogError($"[DodgeHandler] Server validation failed: Dodge cooldown not ready");
                return false;
            }
            
            // 2. MP 확인
            if (character.Mp < InGameConstantData.DODGE_USING_MP_POIONT)
            {
                Debug.LogError($"[DodgeHandler] Server validation failed: Not enough MP for dodge (required: {InGameConstantData.DODGE_USING_MP_POIONT}, current: {character.Mp})");
                return false;
            }
            
            // 3. 이동 불가 상태 확인
            // TODO: 실제 Root 이펙트 타입에 맞게 수정 필요
            // if (character.IsHaveBuff(eEffectType.Root))
            // {
            //     Debug.LogWarning($"[DodgeHandler] Server validation failed: Character is rooted");
            //     return false;
            // }
            
            if (character.IsHaveBuff(eEffectType.Stun) || character.IsHaveBuff(eEffectType.Stiffen))
            {
                Debug.LogError($"[DodgeHandler] Server validation failed: Character is stunned or stiffened");
                return false;
            }
            
            return true;
        }

        public override bool Execute(Character character, ref ActionInfo actionInfo)
        {
            // 서버 검증 후 실행되는 호출에서만 리소스 소비/쿨다운 적용
            if (actionInfo.fromAction == eFromAction.Server)
            {
                character.UseMP(InGameConstantData.DODGE_USING_MP_POIONT);
                character.m_dashCoolTime.CoolTime = character.GetDodgeMaxCoolTime();
            }

            // 파라미터 설정
            _speed = InGameConstantData.DODGE_SPEED;
            float requestedDistance = InGameConstantData.DODGE_RANGE;
            bool isFullDistance;
            _distance = character.CalculateValidDashDistance(requestedDistance, out isFullDistance);

            // 시작/종료 오프셋
            float startOffset = Mathf.Clamp01(character.m_startOffsetForDash);
            float endOffset = Mathf.Clamp01(character.m_endOffsetForDash);

            // 시간 계산 (서버/클라 동기화용)
            float moveDuration = _distance / Mathf.Max(0.0001f, _speed);
            float animMoveSection = Mathf.Max(0.0001f, 1f - startOffset - endOffset);
            _animDuration = moveDuration / animMoveSection;
            _moveStartTime = _animDuration * startOffset;
            _moveEndTime = _animDuration * (1f - endOffset);

            // 시작 상태 저장
            _beginPos = character.GetPosition();
            _dashForward = actionInfo.direction;
            _elapsed = Mathf.Max(0f, (float)(NetworkTime.time - actionInfo.startTime));
            _isRunning = true;

            character.IgnoreDamage = true;
            character.m_isDashing = true;

            //TODO: jx2 - 대시 관련 패시브가 있는듯??? 확인 필요
            character.UpdatePassiveEvent(IPassiveCondition.eEvent.UsingDash);
            character.CancelSkill();

            // 대시 애니메이션 속도 산출 후 공통 세팅 및 재생
            int dashAnimKey = (int)eCharacterAnimState.Dash;
            float dashAnimationLength = dashAnimKey != -1 ? character.GetAnimationLength(dashAnimKey) : 0f;
            float animationSpeed = dashAnimationLength / Mathf.Max(0.0001f, _animDuration);
            return base.Execute(character, ref actionInfo, animationSpeed);
        }

        public override void UpdateAction(Character character, ref ActionInfo actionInfo)
        {
            if (!_isRunning)
                return;

            // 이동 처리
            if (_elapsed >= _moveStartTime && _elapsed <= _moveEndTime)
            {
                float moveSection = Mathf.Max(0.0001f, _moveEndTime - _moveStartTime);
                float moveProgress = Mathf.Clamp01((_elapsed - _moveStartTime) / moveSection);
                Vector3 newPos = _beginPos + _dashForward * (_distance * moveProgress);
                character.SetPosition(newPos);
            }

            _elapsed += Time.deltaTime;

            if (_elapsed >= _animDuration)
            {
                _isRunning = false;
                character.m_isDashing = false;
                character.IgnoreDamage = false;
                character.OnDashComplete();
                // 종료 시점 처리 필요 시 여기에 추가 (예: 무적 해제 등)
            }
        }
    }