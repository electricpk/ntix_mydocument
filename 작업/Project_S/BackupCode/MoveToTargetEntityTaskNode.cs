
using FrameWork.Util;
using System.Collections;
using UnityEngine;
using UnityEngine.AI;

public class MoveToTargetEntityTaskNode : TaskNode
{
    string m_targeKey;

    NavMeshPath m_path = new NavMeshPath();
    public MoveToTargetEntityTaskNode(string targeKey) { m_targeKey = targeKey; }

    protected override ENodeState Task(BehaviorTreeRoot root)
    {
        var targetEntity = root.BlackBoard.GetObj<Entity>(m_targeKey);

        if (targetEntity ==  null || targetEntity.IsDie)
            return ENodeState.ENS_Failure;


        var checkDist = root.Controller.GetRadius() + targetEntity.GetRadius();

        //if(string.IsNullOrEmpty(m_rangeKey) == false && root.BlackBoard.GetBlackBoardValiable(m_rangeKey, out var data))
        //{
        //    checkDist = data.floatValue;
        //}


        if (NavMesh.CalculatePath(root.Controller.GetPosition(), GetTargetPos(root.Controller, targetEntity), NavMesh.AllAreas, m_path))
        {
            root.StartCoroutine(CheckPos(root.Controller, targetEntity, checkDist));
            return ENodeState.ENS_Running;
        }
        else
        {
            return ENodeState.ENS_Failure;
        }
    }

    Vector3 GetTargetPos(Character controller, Entity targetEntity)
    {
        var targetPos = targetEntity.GetPosition();

        if (targetEntity.IsHaveNavMeshObstacle())
        {
            //Ÿ���ÿ� ������ �Է��ֱ⶧���� �ش� ��ġ�� �������� �ش���ġ ���� ���� ���ͷ� ����ġ�� �����;��ҵ��ϴ�
            var direction = GCCommon.GetDirection(targetEntity.GetPosition(), controller.GetPosition());
            targetPos = targetEntity.GetPosition() + direction * (targetEntity.GetRadius() + 1f);

            if( NavMesh.SamplePosition(targetPos, out var hit, 10, NavMesh.AllAreas ))
            {
                targetPos = hit.position;
            }
        }

        return targetPos;
    }


    IEnumerator CheckPos(Character controller, Entity targetEntity, float checkDist)
    {
        while (true)
        {
            yield return null;



            if (NavMesh.CalculatePath(controller.GetPosition(), GetTargetPos(controller, targetEntity), NavMesh.AllAreas, m_path))
            {
                if (m_path.corners.Length > 1)
                {
                    var distance = GCCommon.GetDistance(controller.GetPosition(), targetEntity.GetPosition());
                    var direction = GCCommon.GetDirection(controller.GetPosition(), m_path.corners[1]);

                    
                    if (m_path.corners.Length == 2 && distance < checkDist)
                    {
                        break;
                    }

                    controller.Action(new Character.ActionInfo()
                    {
                        state = Character.eActionState.Move,
                        direction = direction,
                        intParam = -1,
                    });
                }
                else
                {
                    break;
                }
            }
            else
            {
                break;
            }
        }



        controller.Action(new Character.ActionInfo()
        {
            state = Character.eActionState.Idle,
            intParam = -1,
        });



        m_result = ENodeState.ENS_Success;
    }

    //public override IEnumerator Go(BehaviorTreeRoot root)
    //{
    //    return base.Go(root);
    //}

}