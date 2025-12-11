using FrameWork.Util;
using Mirror;
using System;
using System.Collections;
using UnityEngine;

/**
* hkpark 강하늘 구 버전
*/
public class CharacterGang : CharacterMagician
{
    [Header("스킬 팔꿈치 근접 공격 설정")]
    [SerializeField] private Vector3 meleeAttackBoxSize = new Vector3(1f, 1.5f, 0.1f); // 근접 공격 박스 크기
    [SerializeField] private float meleeAttackDistance = 0.8f; // 캐릭터로부터의 거리

    [Header("스킬 발차기 부채꼴 공격 설정")]
    [SerializeField] private float fanAttackRadius = 1.5f; // 부채꼴 공격 반지름
    [SerializeField] private float fanAttackAngle = 120f; // 부채꼴 각도 (도 단위)
    [SerializeField] private float fanAttackKnockbackPow = 3f; // 부채꼴 공격 넉백 파워

    [SerializeField] private AnimationCurveAsset m_activeSkillCurve;

    private Coroutine m_skillMoveCoroutine;

    // 성능 최적화를 위한 캐시 변수들
    private int cachedEnemyLayerMask = -1;
    private float cachedFanAttackHalfAngleCos = -1f;
    private readonly Collider[] hitCollidersBuffer = new Collider[32]; // 콜라이더 배열 재사용

    public override void OnAnimationEnter(AnimatorStateInfo stateInfo)
    {
        if (isServer)
        {
            // 스킬 애니메이션이 시작되는지 확인
            if (IsSkillAnim(stateInfo.shortNameHash))
            {
                // 기존 코루틴이 실행 중이면 중지
                if (m_skillMoveCoroutine != null)
                    StopCoroutine(m_skillMoveCoroutine);

                float animationDuration = GetCurrentAnimationLength();
                float startTime = stateInfo.normalizedTime * animationDuration; // 동기화 스킵시간

                //TODO: jx2 - 적 앞에 있을 경우, 기획 정의 필요.. 적을 지나치지 않아야 될것 같음...
                // 애니메이션 커브에 따라 이동
                m_skillMoveCoroutine = StartCoroutine(MoveByCurves(m_activeSkillCurve, animationDuration, AnimationCurveAsset.Axis.Z, 1f, startTime: startTime));
            }
        }

        base.OnAnimationEnter(stateInfo);
    }

    protected override void OnProcessSkillAttack_Lightweight(LightweightEventParam param)
    {
        base.OnProcessSkillAttack_Lightweight(param);

        if (param.skillId == 90000029)
        {
            switch (param.attackIndex)
            {
                case 0:
                    {
                        PerformMeleeAttack(param.reactionId);
                    }
                    break;

                case 1:
                    {
                        PerformFanAttack(param.reactionId);
                    }
                    break;

                case 2:
                case 3:
                case 4:
                    {
                        var config = new ProjectileSpawnConfig(param.reactionId, true);
                        SpawnProjectileUnified(FindBone("FX_GUN_L"), in config);
                    }
                    break;
                case 5:
                    {
                        var config = new ProjectileSpawnConfig(param.reactionId, true);
                        SpawnProjectileUnified(FindBone("FX_GUN_R"), in config);
                    }
                    break;
            }
        }
        else
        {
            switch (param.attackIndex)
            {
                case 0:
                case 2:
                    {
                        var config = new ProjectileSpawnConfig(param.reactionId, true);
                        SpawnProjectileUnified(FindBone("FX_GUN_R"), in config);
                    }
                    break;

                case 1:
                    {
                        var config = new ProjectileSpawnConfig(param.reactionId, true);
                        SpawnProjectileUnified(FindBone("FX_GUN_L"), in config);
                    }
                    break;

                case 3:
                    {
                        var config = new ProjectileSpawnConfig(param.reactionId, true);
                        SpawnProjectileUnified(FindBone("FX_GUN_L"), in config);
                        SpawnProjectileUnified(FindBone("FX_GUN_R"), in config);
                    }
                    break;
                case 4:
                    {
                        var config = new ProjectileSpawnConfig(param.reactionId, true);
                        SpawnProjectileUnified(FindBone("FX_GUN_L"), in config);
                        SpawnProjectileUnified(FindBone("FX_GUN_R"), in config);
                    }
                    break;
                case 5:
                    {
                    }
                    break;
            }
        }

        //TODO: jx2 - 실제 피격시만 호출??
        OnSuccessSkillAttack_Lightweight(param);
    }

    protected override void OnProcessVFX_Lightweight(LightweightEventParam param)
    {
        if (param.vfxCombinedEventTypeValue == (int)eVFXCombinedEventType.None)
        {
            return;
        }

        switch (param.vfxCombinedEventTypeValue)
        {

            case (int)eVFXCombinedEventType.NormalAttack:
                {
                    Debug.LogError("NormalAttack is not implemented");
                }
                break;

            case (int)eVFXCombinedEventType.SkillAttack:
                {
                    OnProcessSkillAttack_Lightweight(param);
                }
                break;

            default:
                {
                    Debug.LogError("Invalid vfxCombinedEventTypeValue: " + param.vfxCombinedEventTypeValue);
                }
                break;
        }

        base.OnProcessVFX_Lightweight(param);
    }

    #region 근접 공격
    /// <summary>
    /// 캐릭터 앞의 Rect 영역에 있는 적들에게 근접 데미지를 적용
    /// </summary>
#if !USE_TEST
    [Server]
#endif
    protected virtual void PerformMeleeAttack(int reactionId)
    {
        // reactionId를 이용해 넉백 파워 계산
        float knockbackPow = 0;

        if (reactionId > 0)
        {
            var reactionData = FrameWork.Module.Table.TableModule.Get<ReactionDataTable>().Get(reactionId);
            if (reactionData != null && reactionData.Effect.Id != 0)
            {
                if (reactionData.Effect.EffectType == eEffectType.Knockback)
                {
                    knockbackPow = reactionData.EffectParam2;
                }
            }
        }

        // 캐시된 레이어 마스크 사용 또는 계산
        if (cachedEnemyLayerMask == -1)
        {
            cachedEnemyLayerMask = GetEnemyLayerMask();
        }

        // 캐릭터 앞쪽으로 박스 위치 계산 (Vector3 연산 최적화)
        Vector3 characterPos = GetPosition();
        Vector3 characterForward = GetForward();
        Vector3 boxCenter = characterPos + characterForward * meleeAttackDistance;
        boxCenter.y += meleeAttackBoxSize.y * 0.5f;

        // 박스 영역 내의 콜라이더들 검색 (배열 재사용)
        int hitCount = Physics.OverlapBoxNonAlloc(boxCenter, meleeAttackBoxSize * 0.5f, hitCollidersBuffer, transform.rotation, cachedEnemyLayerMask);

        for (int i = 0; i < hitCount; i++)
        {
            Collider hitCollider = hitCollidersBuffer[i];
            Entity hitEntity = hitCollider.GetComponent<Entity>();
            if (hitEntity != null && hitEntity != this && !hitEntity.IsDie && IsEnemyTeam(hitEntity))
            {
                // 데미지 적용
                ApplyMeleeDamage(hitEntity, knockbackPow, characterForward, reactionId);
            }
        }

#if UNITY_EDITOR
        // 항상 디버그 박스 표시 (테스트용)
        var halfSize = meleeAttackBoxSize * 0.5f;
        Debug.DrawLine(boxCenter + new Vector3(-halfSize.x, -halfSize.y, -halfSize.z), boxCenter + new Vector3(halfSize.x, -halfSize.y, -halfSize.z), Color.blue, 3f);
        Debug.DrawLine(boxCenter + new Vector3(-halfSize.x, halfSize.y, -halfSize.z), boxCenter + new Vector3(halfSize.x, halfSize.y, -halfSize.z), Color.blue, 3f);
        Debug.DrawLine(boxCenter + new Vector3(-halfSize.x, -halfSize.y, halfSize.z), boxCenter + new Vector3(halfSize.x, -halfSize.y, halfSize.z), Color.blue, 3f);
        Debug.DrawLine(boxCenter + new Vector3(-halfSize.x, halfSize.y, halfSize.z), boxCenter + new Vector3(halfSize.x, halfSize.y, halfSize.z), Color.blue, 3f);

        // 세로 선들도 추가
        Debug.DrawLine(boxCenter + new Vector3(-halfSize.x, -halfSize.y, -halfSize.z), boxCenter + new Vector3(-halfSize.x, halfSize.y, -halfSize.z), Color.blue, 3f);
        Debug.DrawLine(boxCenter + new Vector3(halfSize.x, -halfSize.y, -halfSize.z), boxCenter + new Vector3(halfSize.x, halfSize.y, -halfSize.z), Color.blue, 3f);
        Debug.DrawLine(boxCenter + new Vector3(-halfSize.x, -halfSize.y, halfSize.z), boxCenter + new Vector3(-halfSize.x, halfSize.y, halfSize.z), Color.blue, 3f);
        Debug.DrawLine(boxCenter + new Vector3(halfSize.x, -halfSize.y, halfSize.z), boxCenter + new Vector3(halfSize.x, halfSize.y, halfSize.z), Color.blue, 3f);

        // 앞뒤 연결선들
        Debug.DrawLine(boxCenter + new Vector3(-halfSize.x, -halfSize.y, -halfSize.z), boxCenter + new Vector3(-halfSize.x, -halfSize.y, halfSize.z), Color.blue, 3f);
        Debug.DrawLine(boxCenter + new Vector3(halfSize.x, -halfSize.y, -halfSize.z), boxCenter + new Vector3(halfSize.x, -halfSize.y, halfSize.z), Color.blue, 3f);
        Debug.DrawLine(boxCenter + new Vector3(-halfSize.x, halfSize.y, -halfSize.z), boxCenter + new Vector3(-halfSize.x, halfSize.y, halfSize.z), Color.blue, 3f);
        Debug.DrawLine(boxCenter + new Vector3(halfSize.x, halfSize.y, -halfSize.z), boxCenter + new Vector3(halfSize.x, halfSize.y, halfSize.z), Color.blue, 3f);

        // 캐릭터 위치에서 박스 중심까지의 선
        Debug.DrawLine(GetPosition(), boxCenter, Color.green, 3f);
#endif
    }

    /// <summary>
    /// 적 레이어 마스크 반환
    /// </summary>
    private int GetEnemyLayerMask()
    {
        return GameLayers.GetEnemyLayerMask(m_team, true);
    }

    /// <summary>
    /// 캐시 초기화 (성능 최적화용)
    /// </summary>
    private void ResetCache()
    {
        cachedEnemyLayerMask = -1;
        cachedFanAttackHalfAngleCos = -1f;
    }

    /// <summary>
    /// 적 팀인지 확인
    /// </summary>
    private bool IsEnemyTeam(Entity targetEntity)
    {
        return targetEntity.Team != this.Team;
    }

    /// <summary>
    /// 근접 데미지 적용
    /// </summary>
    private void ApplyMeleeDamage(Entity targetEntity, float knockbackPow, Vector3 knockbackDirection, int reactionId)
    {
        var currentAttackSetting = GetAttackSetting(m_attackIndex);
        int normalAttackId = currentAttackSetting == null ? 0 : currentAttackSetting.ID;

        var attackInfo = EntityAttackInfo.CreateForReaction(this, reactionId, knockbackDir: knockbackDirection, normalAttackId: normalAttackId);

        targetEntity.SetDamage(attackInfo);

        Debug.Log($"근접 공격: {targetEntity.name}에게 데미지 적용");
    }
    #endregion

    #region 부채꼴 공격
    /// <summary>
    /// 캐릭터 앞쪽 부채꼴 영역에 있는 적들에게 데미지를 적용
    /// </summary>
#if !USE_TEST
    [Server]
#endif
    protected virtual void PerformFanAttack(int reactionId)
    {
        // reactionId를 이용해 넉백 파워 계산
        float knockbackPow = fanAttackKnockbackPow; // 기본 넉백 파워

        if (reactionId > 0)
        {
            var reactionData = FrameWork.Module.Table.TableModule.Get<ReactionDataTable>().Get(reactionId);
            if (reactionData != null && reactionData.Effect.Id != 0)
            {
                if (reactionData.Effect.EffectType == eEffectType.Knockback)
                {
                    knockbackPow = reactionData.EffectParam2;
                }
            }
        }

        // 캐시된 값들 사용 또는 계산
        if (cachedEnemyLayerMask == -1)
        {
            cachedEnemyLayerMask = GetEnemyLayerMask();
        }
        if (cachedFanAttackHalfAngleCos < -0.5f) // 초기값 체크
        {
            cachedFanAttackHalfAngleCos = Mathf.Cos(fanAttackAngle * 0.5f * Mathf.Deg2Rad);
        }

        Vector3 characterPosition = GetPosition();
        Vector3 characterForward = GetForward();

        // 부채꼴 반지름 내의 모든 콜라이더들 검색 (배열 재사용)
        int hitCount = Physics.OverlapSphereNonAlloc(characterPosition, fanAttackRadius, hitCollidersBuffer, cachedEnemyLayerMask);

        for (int i = 0; i < hitCount; i++)
        {
            Collider hitCollider = hitCollidersBuffer[i];
            Entity hitEntity = hitCollider.GetComponent<Entity>();
            if (hitEntity != null && hitEntity != this && !hitEntity.IsDie && IsEnemyTeam(hitEntity))
            {
                // 캐릭터에서 적으로의 방향 벡터 계산 (정규화 최적화)
                Vector3 toTarget = hitEntity.GetPosition() - characterPosition;
                float sqrMagnitude = toTarget.sqrMagnitude;

                // 거리가 너무 가까우면 스킵 (0으로 나누기 방지)
                if (sqrMagnitude < 0.01f) continue;

                Vector3 directionToTarget = toTarget / Mathf.Sqrt(sqrMagnitude);

                // Dot 연산을 사용한 각도 확인 (캐시된 코사인 값 사용)
                float dotProduct = Vector3.Dot(characterForward, directionToTarget);

                // 부채꼴 각도 범위 내에 있는지 확인
                if (dotProduct >= cachedFanAttackHalfAngleCos)
                {
                    // 데미지 적용
                    ApplyFanAttackDamage(hitEntity, knockbackPow, characterPosition, reactionId);
                }
            }
        }

#if UNITY_EDITOR
        // 부채꼴 범위 시각화 (디버그용)
        DrawFanAttackDebug(characterPosition, characterForward);
#endif
    }

    /// <summary>
    /// 부채꼴 공격 데미지 적용
    /// </summary>
    private void ApplyFanAttackDamage(Entity targetEntity, float knockbackPow, Vector3 characterPosition, int reactionId)
    {
        var currentAttackSetting = GetAttackSetting(m_attackIndex);
        int normalAttackId = currentAttackSetting == null ? 0 : currentAttackSetting.ID;

        // 캐릭터로부터 반대 방향으로의 넉백 벡터 계산 (정규화 최적화)
        Vector3 knockBackDirection = targetEntity.GetPosition() - characterPosition;
        float sqrMagnitude = knockBackDirection.sqrMagnitude;

        // 거리가 너무 가까우면 기본 방향 사용
        if (sqrMagnitude < 0.01f)
        {
            knockBackDirection = GetForward();
        }
        else
        {
            knockBackDirection = knockBackDirection / Mathf.Sqrt(sqrMagnitude);
        }

        var attackInfo = EntityAttackInfo.CreateForReaction(this, reactionId, knockbackDir: knockBackDirection, normalAttackId: normalAttackId);

        targetEntity.SetDamage(attackInfo);

        Debug.Log($"부채꼴 공격: {targetEntity.name}에게 데미지 및 넉백 적용 (넉백 방향: {knockBackDirection})");
    }

#if UNITY_EDITOR
    /// <summary>
    /// 부채꼴 공격 범위 디버그 시각화
    /// </summary>
    private void DrawFanAttackDebug(Vector3 center, Vector3 forward)
    {
        // 부채꼴의 좌우 경계선 계산
        float halfAngle = fanAttackAngle * 0.5f;
        Vector3 leftBoundary = Quaternion.AngleAxis(-halfAngle, Vector3.up) * forward * fanAttackRadius;
        Vector3 rightBoundary = Quaternion.AngleAxis(halfAngle, Vector3.up) * forward * fanAttackRadius;

        // 중심에서 좌우 경계까지의 선
        Debug.DrawLine(center, center + leftBoundary, Color.red, 3f);
        Debug.DrawLine(center, center + rightBoundary, Color.red, 3f);

        // 부채꼴 호 그리기 (여러 개의 선분으로 근사)
        int segments = 20;
        float angleStep = fanAttackAngle / segments;
        Vector3 prevPoint = center + leftBoundary;

        for (int i = 1; i <= segments; i++)
        {
            float currentAngle = -halfAngle + (angleStep * i);
            Vector3 currentPoint = center + Quaternion.AngleAxis(currentAngle, Vector3.up) * forward * fanAttackRadius;
            Debug.DrawLine(prevPoint, currentPoint, Color.red, 3f);
            prevPoint = currentPoint;
        }

        // 중심점 표시
        Debug.DrawLine(center + Vector3.up * 0.5f, center - Vector3.up * 0.5f, Color.yellow, 3f);
        Debug.DrawLine(center + Vector3.left * 0.5f, center + Vector3.right * 0.5f, Color.yellow, 3f);
    }
#endif
    #endregion
}