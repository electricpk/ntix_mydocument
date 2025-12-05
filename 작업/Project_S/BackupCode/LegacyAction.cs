    // Character+Server.cs  LegacyAction() 기존 액션 함수 백업

    /// <summary>
    /// 기존 액션 시스템 (호환성을 위해 유지)
    /// </summary>
    private bool LegacyAction(ActionInfo actionInfo)
    {
        if (actionInfo.state != eActionState.Move && actionInfo.state != eActionState.Idle)
        {
            ReflashAutoRecoveryHPTime();
        }

        var beforeSpeed = m_speed.Value;
        UpdateMoveSpeed();

        OnServerCharacterAction?.Invoke(actionInfo);

        //! 공격 타겟은 넘어올때, 유효성 체크를 해서 넘어오니, 여기서 체크할 필요가 없다. 
        // - IsTargetingOK()

        float animationSpeed = 1f;
        m_actionBeginPos = GetPosition();

        switch (actionInfo.state)
        {
            case eActionState.Knockdown:
                {
                    if (m_knockBackCoroutine != null)
                    {
                        StopCoroutine(m_knockBackCoroutine);
                    }

                    m_knockBackCoroutine = null;
                }
                break;
            case eActionState.FixedIdle:
            case eActionState.Idle:
                {
                    if (IsHaveSkillOption(eSkillOption.Move))
                        actionInfo.state = eActionState.SkillMoveStop;
                }
                break;
            case eActionState.SkillReady:
                {
                    var currentSkillInfo = GetCurrentSkillInfo();
                    if (currentSkillInfo?.Data.IsChargeSkill == true && m_isCheckChargeGauge == false)
                    {
                        m_isCheckChargeGauge = true;
                        m_skillChargeGaugeTime = NetworkTime.time;
                    }
                }
                break;
            case eActionState.Move:
            case eActionState.FixedMove:
                {
                    // animationSpeed = m_speed.Value / m_statData.MoveSpeed;   // 달리기 애니메이션 속도는 Animator 파라미터로 결정됨
                }
                break;
            case eActionState.Dash:
                {
                    
                }
                break;
            case eActionState.Die:
                {
                    CancelSkill();
                }
                break;
            case eActionState.Attack:
                {
                    Debug.Log($"<color=green>[Server] Action - Attack Case START</color>");

                    actionInfo.intParam = m_attackIndex;
                    SetTargetEntity(actionInfo.targetEntity);

                    if (actionInfo.targetEntity != null && actionInfo.targetEntity.IsDie == false)
                    {
                        m_attackTargetPos = actionInfo.targetEntity.GetPosition();
                    }
                    else
                    {
                        SetTargetEntity(null);
                        m_attackTargetPos = GetPosition() + actionInfo.direction * GetCurrentAttackRange();
                    }

                    var animationName = GetStateToAnimatioName(eActionState.Attack, actionInfo.intParam);
                    animationSpeed = GetAttackSpeed((int)animationName);
                    Debug.Log($"    Animation: {animationName}, Speed: {animationSpeed}");

                    m_attackCoolTime.CoolTime = GetAttackDelay();
                    UseMP(this.CharacterStatsData.AttackStaminaValue);
                    actionInfo.vector3Param = m_attackTargetPos;
                }
                break;
            case eActionState.SkillCancle:
                {
                    if (beforeSpeed > 0)
                    {
                        // UpdateMoveSpeed();
                        // animationSpeed = (float)m_speed.Value / m_statData.MoveSpeed;   // 달리기 애니메이션 속도는 Animator 파라미터로 결정됨
                    }

                    var currentSkillInfo = GetCurrentSkillInfo();
                    if (currentSkillInfo?.Data.IsChargeSkill == true)
                    {
                        ResetSkillCoolTime(true);
                    }

                    m_isCheckChargeGauge = false;
                }
                break;
            case eActionState.Skill:
                {
                    RpcResetCombo();

                    var currentSkillInfo = GetCurrentSkillInfo();
                    if (currentSkillInfo == null)
                    {
                        Debug.LogError($"[Server] Action - Skill Case - currentSkillInfo is null, skillIndex: {actionInfo.intParam}");
                        return false;
                    }

                    if (currentSkillInfo.IsMaintain == true)
                    {
                        if (!currentSkillInfo.Data.IsChargeSkill)
                        {
                            SetSkillMaintain(actionInfo);
                        }

                        actionInfo.state = eActionState.SkillMainTain;
                    }
                    else
                    {
                        int skillIndex = GetCurrentSkillIndex();
                        bool isSpecialSkill = skillIndex == SpecialSkillIndex;

                        actionInfo.intParam = skillIndex;
                        SetTargetEntity(actionInfo.targetEntity);

                        if (isSpecialSkill)
                        {
                            UseSpecialSkillPoint();
                            RpcOnStartSpecialSkill(actionInfo);
                        }

                        ResetSkillCoolTime();
                        UseMP(currentSkillInfo.Data.Point);

                        if (currentSkillInfo.Data.IsChargeSkill == true && m_isCheckChargeGauge == false)
                        {
                            m_isCheckChargeGauge = true;
                            m_skillChargeGaugeTime = NetworkTime.time;
                        }

                        if (!actionInfo.vector3Param.IsZero())
                        {
                            m_attackTargetPos = actionInfo.vector3Param;
                        }
                        else
                        {
                            if (currentSkillInfo.Data.SkillShape.SkillShapeType == eSkillShape.Circle || currentSkillInfo.Data.SkillShape.SkillLength == 0)
                            {
                                m_attackTargetPos = GetPosition() + m_direction * GetRadius();
                            }
                            else
                            {
                                m_attackTargetPos = GetPosition() + m_direction * (currentSkillInfo.Data.SkillShape.SkillLength);
                            }
                        }

                        if (currentSkillInfo.Data.SkillShape.SkillLength > 0 &&
                            GCCommon.GetDistance(m_attackTargetPos, GetPosition()) >= currentSkillInfo.Data.SkillShape.SkillLength)
                        {
                            var tempDir = GCCommon.GetDirection(GetPosition(), m_attackTargetPos);
                            m_attackTargetPos = GetPosition() + tempDir * currentSkillInfo.Data.SkillShape.SkillLength;
                        }

                        if (currentSkillInfo.Data.SkillShape.SkillShapeType == eSkillShape.None)
                        {
                            actionInfo.vector3Param = Vector3.zero;
                        }
                        else
                        {
                            actionInfo.vector3Param = m_attackTargetPos;
                        }

                        if (currentSkillInfo.Data.IsChargeSkill != true)
                        {
                            SetSkillMaintain(actionInfo);
                        }

                        UpdatePassiveEvent(IPassiveCondition.eEvent.UsingSkill);
                    }
                }
                break;
        }

        switch (actionInfo.state)
        {
            case eActionState.Dash:
            case eActionState.Stun:
            case eActionState.Knockdown:
            case eActionState.Die:
            case eActionState.Jump:
            case eActionState.Stiffen:
            case eActionState.AirBorne:
                {
                    RpcResetCombo();

                    SetSkillOption((int)eSkillOption.None);
                    var currentSkillInfo = GetCurrentSkillInfo();
                    if (currentSkillInfo?.IsMaintain == true)
                    {
                        currentSkillInfo.MaintainTime = 0;
                    }
                }
                break;
        }

        var direction = CalcDirection(actionInfo);
        m_direction = direction.normalized;
        UpdateMoveDir(actionInfo);

        if (IsHaveBuff(eEffectType.Confusion))
        {
            m_direction *= -1f;
            m_moveDirection *= -1f;
        }

        actionInfo.direction = direction;
        m_currentAction = actionInfo.state;
        actionInfo.rotationLock = IsHaveSkillOption(eSkillOption.RotationLock);

        if (actionInfo.rotationLock == false)
        {
            SetRotation(Quaternion.LookRotation(m_direction));
        }

        SetProcessAnimation(actionInfo, animationSpeed);
        RpcAction(actionInfo, animationSpeed);

        return true;
    }