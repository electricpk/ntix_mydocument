/**

#define CHARACTER_MOVE_DEBUG

using System.Collections.Generic;
using FrameWork.Util;
using Mirror;
using UnityEngine;

public partial class Character
{

    public struct InputState
    {
        public uint tick;
        public Vector3 movement;
    }

    public struct MovementState
    {
        public uint tick;
        public Vector3 position;
        public Quaternion rotation;
        public Vector3 velocity;
        public eActionState actionState;
        public eCharacterAnimState animState;
    }


    // --- 커스텀 틱 시스템 변수 ---
    private float _tickRate = 0.016667f; // 60hz (1/60s)
    private float _tickDeltaTime;
    private uint _currentTick;

    // --- 서버 측 변수 (Server-Side) ---
    private Queue<InputState> _serverInputQueue = new Queue<InputState>();
    private MovementState[] _serverStateBuffer = new MovementState[1024];
    private uint _serverLastProcessedTick;

    // --- 클라이언트 측 변수 (Client-Side) ---
    private List<InputState> _clientInputHistory = new List<InputState>();
    private List<MovementState> _clientStateHistory = new List<MovementState>();
    private InputState _currentInputState;
    private MovementState _lastServerMovementState;
    private bool _isReconciling = false;
    // 서버가 보낸 최신 벨로시티(비소유자/동기화용)
    protected Vector3 m_serverVelocity = Vector3.zero;
    
    #region Mirror Callbacks
    // public override void OnStartClient()
    // {
    //     base.OnStartClient();

    //     // 모든 클라이언트에서 NetworkTransform 사용을 중단하고 수동 동기화로 전환합니다.
    //     if (TryGetComponent<NetworkTransformBase>(out var netTransform))
    //     {
    //         netTransform.enabled = false;
    //         Log("Disabled NetworkTransform for manual synchronization.");
    //     }
    // }
    #endregion

    #region Unity Lifecycle
    private void UpdateMove()
    {
        if (isOwned && isClient)
        {
            SampleLocalInput();
        }

        _tickDeltaTime += Time.deltaTime;
        while (_tickDeltaTime >= _tickRate)
        {
            _tickDeltaTime -= _tickRate;
            Tick();
        }
    }

    private void Tick()
    {
        _currentTick++;
        // Log($"Tick {_currentTick} processing..."); // 너무 많은 로그를 유발할 수 있어 주석 처리

        if (isServer)
        {
            Server_ProcessInputs();
        }

        if (isOwned && isClient)
        {
            Client_ProcessMovement();
        }

        if (isServer)
        {
            Server_SendStateToClients();
        }
    }
    #endregion


    #region 클라이언트 로직 (Client Logic)

    private void SampleLocalInput()
    {
        if (InGameDB.InGameUser == null)
        {
            _currentInputState.movement = Vector3.zero;
            return;
        }

        _currentInputState.movement = InGameDB.InGameUser.MoveDirection.ToWorldDir();
    }

    private void Client_ProcessMovement()
    {
        _currentInputState.tick = _currentTick;
        CmdSendInput(_currentInputState);

        // 호스트(isServer)인 경우, 클라이언트 예측 로직을 건너뜁니다.
        // 서버 로직의 결과가 즉시 반영되므로 예측이 필요 없습니다.
        if (isServer)
        {
            return;
        }

        if (_isReconciling)
        {
            Log($"Client Tick {_currentTick}: Skipping prediction, currently reconciling.");
            return;
        }

        // 이전 상태 가져오기
        MovementState beforeState = _clientStateHistory.Count > 0 ?
            _clientStateHistory[_clientStateHistory.Count - 1] :
            new MovementState { tick = _currentTick -1, position = transform.position, rotation = transform.rotation, velocity = Vector3.zero };

        // 현재 클라이언트 예측 상태 적용
        var (actionState, animState) = GetCurrentActionAndAnimState();
        beforeState.actionState = actionState;
        beforeState.animState = animState;

        // 이동 - 클라이언트 예측 처리
        MovementState predictedState = ProcessMovement(_currentInputState, beforeState, true);
        ApplyState(predictedState); // 클라이언트 예측 상태 적용

        _clientInputHistory.Add(_currentInputState);
        _clientStateHistory.Add(predictedState);
    }

    [Command]
    private void CmdSendInput(InputState input)
    {
        // Log($"Server: Received input for tick {input.tick} from client."); // 너무 많은 로그
        if (input.tick <= _serverLastProcessedTick)
        {
            Log($"Server: Discarding stale input for tick {input.tick} (last processed: {_serverLastProcessedTick}).");
            return;
        }
        _serverInputQueue.Enqueue(input);
    }

    [ClientRpc]
    public void RpcReceiveServerState(MovementState serverState)
    {
        // 소유자(입력 보낸 클라이언트)인 경우에는 리콘실리에이션 로직 사용
        if (isOwned && isClient)
        {
            _lastServerMovementState = serverState;
            Client_Reconcile();
            return;
        }

        // 비소유자(다른 클라이언트)인 경우에는 서버 권한 상태를 곧바로 적용.
        // 필요하면 부드러운 보간(interpolation)으로 개선할 수 있습니다.
        ApplyState(serverState);
    }

    private void Client_Reconcile()
    {
        if (_isReconciling) return;
        
        _isReconciling = true;

        int historyIndex = _clientStateHistory.FindIndex(state => state.tick == _lastServerMovementState.tick);

        if (historyIndex == -1)
        {
            // Log($"Client Reconcile: Could not find history for tick {_lastServerMovementState.tick}. Aborting.");
            _isReconciling = false;
            return;
        }

        MovementState predictedStateInHistory = _clientStateHistory[historyIndex];
        float positionError = Vector3.Distance(predictedStateInHistory.position, _lastServerMovementState.position);
        bool stateMismatch = !EqualsActionAndAnimState(predictedStateInHistory, _lastServerMovementState);
        bool isValid = IsValidMove(predictedStateInHistory) && IsValidMove(_lastServerMovementState);

        if (positionError > 0.01f || isValid == false)
        {
            Log($"Client Reconcile: Misprediction at tick {_lastServerMovementState.tick}! PosError: {positionError:F4}, StateMismatch: {stateMismatch}. ValidMove: {isValid}");
            Log($"  - Server Pos: {_lastServerMovementState.position.ToString("F3")}, Predicted Pos: {predictedStateInHistory.position.ToString("F3")}");
            Log($"  - Server State: {_lastServerMovementState.actionState}/{_lastServerMovementState.animState}, Predicted State: {predictedStateInHistory.actionState}/{predictedStateInHistory.animState}");

            // 서버의 최종 상태로 강제 설정
            ApplyState(_lastServerMovementState);
            Log($"  - Corrected position to server's state.");

            // 서버가 확인한 틱 이후의 모든 입력을 재실행
            MovementState replayedBaseState = _lastServerMovementState;
            for (int i = historyIndex + 1; i < _clientStateHistory.Count; i++)
            {
                InputState inputToReplay = _clientInputHistory[i];
                Log($"  - Replaying input for tick {inputToReplay.tick}");
                MovementState replayedState = ProcessMovement(inputToReplay, replayedBaseState, true);
                _clientStateHistory[i] = replayedState; // 히스토리도 수정된 예측으로 덮어쓰기
                replayedBaseState = replayedState;
            }

            // 최종 재실행 결과 적용
            ApplyState(replayedBaseState);
            Log($"  - Finished replaying inputs. Final reconciled position: {replayedBaseState.position.ToString("F3")}");
        }
        else
        {
            // Log($"Client Reconcile: Prediction for tick {_lastServerMovementState.tick} was correct. No correction needed.");
        }

        // 오래된 히스토리 삭제
        if (_clientInputHistory.Count > 0 && _clientStateHistory.Count > 0)
        {
            _clientInputHistory.RemoveAll(input => input.tick <= _lastServerMovementState.tick);
            _clientStateHistory.RemoveAll(state => state.tick <= _lastServerMovementState.tick);
        }

        _isReconciling = false;
    }

    #endregion

    #region 서버 로직 (Server Logic)

    private void Server_ProcessInputs()
    {
        while (_serverInputQueue.Count > 0)
        {
            InputState input = _serverInputQueue.Dequeue();

            MovementState currentState = _serverLastProcessedTick > 0 ?
                _serverStateBuffer[_serverLastProcessedTick % _serverStateBuffer.Length] :
                new MovementState { tick = input.tick -1, position = transform.position, rotation = transform.rotation, velocity = Vector3.zero };

            // 서버 현재 액션/애님 상태 가져오기
            var (actionState, animState) = GetCurrentActionAndAnimState();
            currentState.actionState = actionState;
            currentState.animState = animState;

            // 이동 처리 - 서버에서만 실제 이동 적용
            MovementState newState = ProcessMovement(input, currentState, false);
            
            // 서버 측 캐릭터의 상태를 실제로 적용합니다.
            ApplyState(newState);

            _serverStateBuffer[input.tick % _serverStateBuffer.Length] = newState;
            _serverLastProcessedTick = input.tick;
        }
    }

    private void Server_SendStateToClients()
    {
        if (_serverLastProcessedTick > 0)
        {
            MovementState stateToSend = _serverStateBuffer[_serverLastProcessedTick % _serverStateBuffer.Length];
            // Log($"Server: Sending state for tick {stateToSend.tick} to client. Position: {stateToSend.position}"); // 너무 많은 로그
            // Broadcast state to all clients (including owner). Owner will reconcile, others will apply directly.
            RpcReceiveServerState(stateToSend);
        }
    }

    #endregion

    #region 공통 로직 (Common Logic)

    private MovementState ProcessMovement(InputState input, MovementState currentState, bool isClientPrediction)
    {
        float speed = GetMoveSpeedByState(currentState.actionState, currentState.animState, input.movement);
        // Log($"ProcessMovement (Tick: {input.tick}): Input={input.movement}, Speed={speed}, CurrentPos={currentState.position}"); // 너무 많은 로그

        if (speed <= 0f || input.movement == Vector3.zero)
        {
            return new MovementState
            {
                tick = input.tick,
                position = currentState.position, // 현재 위치 그대로 반환
                rotation = currentState.rotation,
                velocity = Vector3.zero,
                actionState = currentState.actionState,
                animState = currentState.animState,
            };
        }

        Vector3 moveVector = input.movement.normalized * speed;
        Vector3 moveOffset = moveVector * _tickRate;
        Vector3 newPosition = currentState.position + moveOffset;
        
        // 회전 처리
        Quaternion newRotation = currentState.rotation;
        if (input.movement != Vector3.zero)
        {
            newRotation = Quaternion.LookRotation(input.movement.normalized);
        }

        return new MovementState
        {
            tick = input.tick,
            position = newPosition,
            rotation = newRotation,
            velocity = moveVector,
            actionState = currentState.actionState,
            animState = currentState.animState,
        };
    }

    private void ApplyState(MovementState state)
    {
        // 위치 이동
        if (Vector3.Distance(transform.position, state.position) > 0.01f)
        {
            Vector3 offset = state.position - transform.position;
            MoveOffset(offset);
        }
        
        // 회전 적용
        if (Quaternion.Angle(transform.rotation, state.rotation) > 0.1f)
        {
            transform.rotation = state.rotation;
        }
        // 서버에서 전달된 상태의 벨로시티는 비소유자(원격) 애니메이션에 사용하도록 저장
        m_serverVelocity = state.velocity;
    }

    private (eActionState, eCharacterAnimState) GetCurrentActionAndAnimState()
    {
        m_actionHandlerManager.GetCurrentAction(out var actionState, out var actionInfo);
        var animState = AnimHashToAnimState(CurrentPlayAnimHash);

        return (actionState, animState);
    }

    private bool EqualsActionAndAnimState(MovementState a, MovementState b)
    {
        return a.actionState == b.actionState && a.animState == b.animState;
    }

    private bool IsValidMove(MovementState state)
    {
        bool isValidAnim = state.animState switch
        {
            eCharacterAnimState.Idle or
            eCharacterAnimState.Idle_LowerBody or
            eCharacterAnimState.Run => true,
            _ => false,
        };

        bool isValidAction = state.actionState switch
        {
            eActionState.Idle or
            eActionState.FixedIdle or
            eActionState.Move => true,
            _ => false,
        };

        return isValidAnim && isValidAction;
    }

    #endregion

    #region Debugging

    private void Log(string message)
    {
#if CHARACTER_MOVE_DEBUG
        Debug.Log($"[CharacterMove:{m_userSeq}:{(isServer ? "S" : "C")}] {message}");
#endif
    }

    #endregion
}
*/
