using System;

namespace CodexEngine.Runtime;

public static class Mathf
{
    public const float PI = MathF.PI, Deg2Rad = PI / 180f, Rad2Deg = 180f / PI;
    public const float Infinity = float.PositiveInfinity, Epsilon = float.Epsilon;
    public static float Abs(float x) => Math.Abs(x);
    public static int Abs(int x) => Math.Abs(x);
    public static float Atan2(float y, float x) => MathF.Atan2(y, x);
    public static float Asin(float x) => MathF.Asin(x);
    public static float Cos(float x) => MathF.Cos(x);
    public static float Sin(float x) => MathF.Sin(x);
    public static float Tan(float x) => MathF.Tan(x);
    public static float Log(float x) => MathF.Log(x);
    public static float Exp(float x) => MathF.Exp(x);
    public static float Sqrt(float x) => MathF.Sqrt(x);
    public static float Round(float x) => MathF.Round(x);
    public static int RoundToInt(float x) => (int)MathF.Round(x);
    public static int CeilToInt(float x) => (int)MathF.Ceiling(x);
    public static int FloorToInt(float x) => (int)MathF.Floor(x);
    public static float Sign(float x) => x >= 0 ? 1 : -1;
    public static float Max(float a, float b) => Math.Max(a, b);
    public static float Min(float a, float b) => Math.Min(a, b);
    public static int Max(int a, int b) => Math.Max(a, b);
    public static int Min(int a, int b) => Math.Min(a, b);
    public static float Max(float a, float b, float c) => Max(Max(a,b),c);
    public static float Min(float a, float b, float c) => Min(Min(a,b),c);
    public static float Clamp(float x,float lo,float hi) => x < lo ? lo : x > hi ? hi : x;
    public static int Clamp(int x,int lo,int hi) => x < lo ? lo : x > hi ? hi : x;
    public static float Clamp01(float x) => Clamp(x,0,1);
    public static float LerpUnclamped(float a,float b,float t) => a+(b-a)*t;
    public static float Lerp(float a,float b,float t) => LerpUnclamped(a,b,Clamp01(t));
    public static float InverseLerp(float a,float b,float x) => a == b ? 0 : Clamp01((x-a)/(b-a));
    public static float Repeat(float x,float length) => Clamp(x-MathF.Floor(x/length)*length,0,length);
    public static float DeltaAngle(float a,float b) { float d=Repeat(b-a,360); return d>180 ? d-360 : d; }
    public static float LerpAngle(float a,float b,float t) => a+DeltaAngle(a,b)*Clamp01(t);
    public static float MoveTowards(float a,float b,float step) => Abs(b-a)<=step ? b : a+Sign(b-a)*step;
    public static float MoveTowardsAngle(float a,float b,float step) => MoveTowards(a,a+DeltaAngle(a,b),step);
    public static float SmoothStep(float a,float b,float t) { t=Clamp01(t);return LerpUnclamped(a,b,t*t*(3-2*t)); }
    public static float SmoothDampAngle(float current,float target,ref float velocity,float time,float maxSpeed,float dt)
        => SmoothDamp(current,current+DeltaAngle(current,target),ref velocity,time,maxSpeed,dt);
    public static float SmoothDamp(float current,float target,ref float velocity,float time,float maxSpeed,float dt)
    {
        time=Max(.0001f,time);
        float omega=2/time, x=omega*dt, decay=1/(1+x+.48f*x*x+.235f*x*x*x);
        float change=Clamp(current-target,-maxSpeed*time,maxSpeed*time);
        float temp=(velocity+omega*change)*dt;
        velocity=(velocity-omega*temp)*decay;
        float result=(current-change)+(change+temp)*decay;
        if ((target-current>0)==(result>target)) { velocity=0; return target; }
        return result;
    }
}

