
    public class SpeedHistory
    {
        private readonly List<(double time, float speed)> history = new();
        private readonly double keepDuration = 60f; // 1분 보관

        // speed 값 추가
        public void Add(float speed)
        {
            double now = NetworkTime.time;

            if (history.Count > 0 && history[^1].time == now)
            {
                history[^1] = (now, speed);
            }
            else
            {
                history.Add((now, speed));
            }

            RemoveOld(now);
        }

        // 특정 시간 t에서 speed 조회 (floor 방식, 뒤에서부터 검색)
        public float Get(double t)
        {
            if (history.Count == 0) 
                return 0f;

            for (int i = history.Count - 1; i >= 0; i--)
            {
                if (history[i].time <= t)
                {
                    return history[i].speed;
                }
            }

            // t보다 작은 값이 전혀 없으면 가장 오래된 값 반환
            return history[0].speed;
        }

        // 오래된 데이터 제거
        private void RemoveOld(double now)
        {
            while (history.Count > 0 && now - history[0].time > keepDuration)
            {
                history.RemoveAt(0); // 오래된 데이터 버리기
            }
        }
    }