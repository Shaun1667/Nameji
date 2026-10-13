using System.Collections.Generic;

namespace Shimeji.Core
{
    /// <summary>
    /// 클릭 통과(WS_EX_TRANSPARENT) 토글 결정.
    /// 버튼이 눌린 동안은 눌림 시작 시점의 판정을 고정한다
    /// (캐릭터를 잡고 빠르게 움직여도 놓치지 않고, 바탕화면 드래그 중 캐릭터 위를 지나도 가로채지 않음).
    /// </summary>
    public sealed class ClickThroughController
    {
        bool _latched;
        bool _latchedCapture;
        bool _capturing;

        public bool Capturing => _capturing;

        /// <summary>이번 프레임에 오버레이가 마우스를 받아야 하면 true.</summary>
        public bool Update(bool cursorOverInteractive, bool anyButtonDown)
        {
            if (anyButtonDown)
            {
                if (!_latched)
                {
                    _latched = true;
                    _latchedCapture = cursorOverInteractive;
                }
                _capturing = _latchedCapture;
            }
            else
            {
                _latched = false;
                _capturing = cursorOverInteractive;
            }
            return _capturing;
        }
    }

    public enum PointerEventType { Click, DoubleClick, DragStart, DragMove, DragEnd, RightClick }

    public struct PointerEvent
    {
        public PointerEventType Type;
        /// <summary>대상 ID (캐릭터 등). -1 = 없음.</summary>
        public int TargetId;
        public Vec2 Position;
        /// <summary>DragEnd 시 최근 이동 속도 (px/s).</summary>
        public Vec2 Velocity;

        public override string ToString() => $"{Type} target={TargetId} pos={Position} vel={Velocity}";
    }

    /// <summary>
    /// 폴링한 마우스 상태로 클릭/더블클릭/드래그/우클릭 이벤트를 만든다. 던지기 속도는 최근 샘플 평균.
    /// </summary>
    public sealed class PointerTracker
    {
        public float DragThreshold = 6f;
        public float DoubleClickTime = 0.35f;
        public float VelocityWindow = 0.08f;

        bool _leftWasDown, _rightWasDown;
        int _pressTarget = -1;
        int _rightPressTarget = -1;
        Vec2 _pressPos;
        bool _dragging;
        double _lastClickTime = -10;
        int _lastClickTarget = -1;
        readonly List<(double t, Vec2 p)> _samples = new List<(double, Vec2)>();

        public bool Dragging => _dragging;
        public int DragTarget => _dragging ? _pressTarget : -1;
        public Vec2 PressPosition => _pressPos;

        /// <summary>
        /// hitTarget: 커서 아래 대상 ID(-1 = 없음). 눌림 시작 시점의 대상이 드래그/클릭 대상이 된다.
        /// </summary>
        public void Update(double time, Vec2 cursor, bool leftDown, bool rightDown, int hitTarget, List<PointerEvent> output)
        {
            _samples.Add((time, cursor));
            while (_samples.Count > 2 && time - _samples[0].t > VelocityWindow) _samples.RemoveAt(0);

            // --- 왼쪽 버튼 ---
            if (leftDown && !_leftWasDown)
            {
                _pressTarget = hitTarget;
                _pressPos = cursor;
                _dragging = false;
            }
            else if (leftDown && _leftWasDown && _pressTarget >= 0)
            {
                if (!_dragging && (cursor - _pressPos).Length >= DragThreshold)
                {
                    _dragging = true;
                    output.Add(new PointerEvent { Type = PointerEventType.DragStart, TargetId = _pressTarget, Position = _pressPos });
                }
                if (_dragging)
                    output.Add(new PointerEvent { Type = PointerEventType.DragMove, TargetId = _pressTarget, Position = cursor });
            }
            else if (!leftDown && _leftWasDown)
            {
                if (_pressTarget >= 0)
                {
                    if (_dragging)
                    {
                        output.Add(new PointerEvent { Type = PointerEventType.DragEnd, TargetId = _pressTarget, Position = cursor, Velocity = CurrentVelocity() });
                    }
                    else
                    {
                        bool dbl = _lastClickTarget == _pressTarget && time - _lastClickTime <= DoubleClickTime;
                        output.Add(new PointerEvent { Type = dbl ? PointerEventType.DoubleClick : PointerEventType.Click, TargetId = _pressTarget, Position = cursor });
                        _lastClickTime = dbl ? -10 : time;
                        _lastClickTarget = dbl ? -1 : _pressTarget;
                    }
                }
                _pressTarget = -1;
                _dragging = false;
            }

            // --- 오른쪽 버튼 (뗄 때 발생) ---
            if (rightDown && !_rightWasDown) _rightPressTarget = hitTarget;
            else if (!rightDown && _rightWasDown)
            {
                if (_rightPressTarget >= 0)
                    output.Add(new PointerEvent { Type = PointerEventType.RightClick, TargetId = _rightPressTarget, Position = cursor });
                _rightPressTarget = -1;
            }

            _leftWasDown = leftDown;
            _rightWasDown = rightDown;
        }

        public Vec2 CurrentVelocity()
        {
            if (_samples.Count < 2) return Vec2.Zero;
            var a = _samples[0];
            var b = _samples[_samples.Count - 1];
            double dt = b.t - a.t;
            if (dt <= 1e-4) return Vec2.Zero;
            return (b.p - a.p) / (float)dt;
        }

        /// <summary>대상이 사라졌을 때(캐릭터 제거 등) 진행 중인 드래그를 취소.</summary>
        public void Cancel()
        {
            _pressTarget = -1;
            _rightPressTarget = -1;
            _dragging = false;
        }
    }
}
