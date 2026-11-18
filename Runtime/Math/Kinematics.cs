using System;
using N = System.Numerics;
using System.Runtime.InteropServices;
using System.Text.Json.Serialization;

namespace CodexEngine.Runtime;

[StructLayout(LayoutKind.Sequential)]
public struct Vector2
{
    public float x,y;
    public Vector2(float x,float y){this.x=x;this.y=y;}
    public static Vector2 zero=>default;
    public readonly float sqrMagnitude=>x*x+y*y;
    public readonly float magnitude=>MathF.Sqrt(sqrMagnitude);
    public readonly Vector2 normalized=>magnitude>1e-5f?this/magnitude:default;
    public static Vector2 operator +(Vector2 a,Vector2 b)=>new(a.x+b.x,a.y+b.y);
    public static Vector2 operator -(Vector2 a,Vector2 b)=>new(a.x-b.x,a.y-b.y);
    public static Vector2 operator *(Vector2 a,float b)=>new(a.x*b,a.y*b);
    public static Vector2 operator /(Vector2 a,float b)=>new(a.x/b,a.y/b);
    public static Vector2 ClampMagnitude(Vector2 a,float max)=>a.sqrMagnitude>max*max?a.normalized*max:a;
}

[StructLayout(LayoutKind.Sequential)]
public struct Vector3 : IEquatable<Vector3>
{
    public float x,y,z;
    public Vector3(float x,float y,float z){this.x=x;this.y=y;this.z=z;}
    public static Vector3 zero => default;
    public static Vector3 one => new(1,1,1);
    public static Vector3 up => new(0,1,0);
    public static Vector3 down => new(0,-1,0);
    public static Vector3 forward => new(0,0,1);
    public static Vector3 back => new(0,0,-1);
    public static Vector3 right => new(1,0,0);
    public static Vector3 left => new(-1,0,0);
    public readonly float sqrMagnitude => x*x+y*y+z*z;
    public readonly float magnitude => MathF.Sqrt(sqrMagnitude);
    public readonly Vector3 normalized { get { float length=magnitude;return length>1e-5f?this/length:default; } }
    public void Normalize()=>this=normalized;
    public readonly N.Vector3 ToNumerics()=>new(x,y,z);
    public static Vector3 FromNumerics(N.Vector3 v)=>new(v.X,v.Y,v.Z);
    public static Vector3 operator +(Vector3 a, Vector3 b)=>new(a.x+b.x,a.y+b.y,a.z+b.z);
    public static Vector3 operator -(Vector3 a, Vector3 b)=>new(a.x-b.x,a.y-b.y,a.z-b.z);
    public static Vector3 operator -(Vector3 v)=>new(-v.x,-v.y,-v.z);
    public static Vector3 operator *(Vector3 a, float b)=>new(a.x*b,a.y*b,a.z*b);
    public static Vector3 operator *(float b,Vector3 a)=>a*b;
    public static Vector3 operator /(Vector3 a,float b)=>new(a.x/b,a.y/b,a.z/b);
    public static bool operator ==(Vector3 a,Vector3 b)=>(a-b).sqrMagnitude<1e-10f;
    public static bool operator !=(Vector3 a,Vector3 b)=>!(a==b);
    public readonly bool Equals(Vector3 other)=>x==other.x&&y==other.y&&z==other.z;
    public override readonly bool Equals(object obj)=>obj is Vector3 v && Equals(v);
    public override readonly int GetHashCode()=>HashCode.Combine(x,y,z);
    public override readonly string ToString()=>$"({x:G4},{y:G4},{z:G4})";
    public static float Dot(Vector3 a,Vector3 b)=>a.x*b.x+a.y*b.y+a.z*b.z;
    public static Vector3 Cross(Vector3 a,Vector3 b)=>new(a.y*b.z-a.z*b.y,a.z*b.x-a.x*b.z,a.x*b.y-a.y*b.x);
    public static float Distance(Vector3 a,Vector3 b)=>(a-b).magnitude;
    public static float Angle(Vector3 a,Vector3 b){float length=MathF.Sqrt(a.sqrMagnitude*b.sqrMagnitude);return length<1e-15f?0:MathF.Acos(Math.Clamp(Dot(a,b)/length,-1,1))*Mathf.Rad2Deg;}
    public static Vector3 Lerp(Vector3 a,Vector3 b,float t)=>LerpUnclamped(a,b,Mathf.Clamp01(t));
    public static Vector3 LerpUnclamped(Vector3 a,Vector3 b,float t)=>a+(b-a)*t;
    public static Vector3 Project(Vector3 a,Vector3 b)=>b.sqrMagnitude>float.Epsilon?b*(Dot(a,b)/b.sqrMagnitude):default;
    public static Vector3 ClampMagnitude(Vector3 a,float max)=>a.sqrMagnitude>max*max?a.normalized*max:a;
    public static Vector3 MoveTowards(Vector3 a,Vector3 b,float max){var d=b-a;float l=d.magnitude;return l==0||l<=max?b:a+d*(max/l);}
    public static Vector3 RotateTowards(Vector3 a,Vector3 b,float maxRadians,float maxMagnitude)
    {
        float la=a.magnitude,lb=b.magnitude,length=Mathf.MoveTowards(la,lb,maxMagnitude);
        if(la<1e-6f || lb<1e-6f)return MoveTowards(a,b,maxMagnitude);
        var from=a/la;var to=b/lb;
        float angle=MathF.Acos(Math.Clamp(Dot(from,to),-1,1));
        if(angle<1e-6f || angle<=maxRadians)return to*length;
        Vector3 axis=Cross(from,to); if(axis.sqrMagnitude<1e-10f)axis=Cross(from,Math.Abs(from.y)<.9f?up:right);
        return Quaternion.AngleAxis(Math.Min(maxRadians,angle)*Mathf.Rad2Deg,axis.normalized)*from*length;
    }
    public static Vector3 SmoothDamp(Vector3 current,Vector3 target,ref Vector3 velocity,float time,float maxSpeed,float dt)
    {
        time=Math.Max(.0001f,time);float omega=2/time,x=omega*dt,decay=1/(1+x+.48f*x*x+.235f*x*x*x);
        var d=ClampMagnitude(current-target,maxSpeed*time);var at=current-d;var temp=(velocity+omega*d)*dt;
        velocity=(velocity-omega*temp)*decay;var result=at+(d+temp)*decay;
        if(Dot(target-current,result-target)>0){velocity=zero;return target;}return result;
    }
}

public struct Quaternion
{
    public float x,y,z,w;
    public Quaternion(float x,float y,float z,float w){this.x=x;this.y=y;this.z=z;this.w=w;}
    public static bool operator ==(Quaternion a, Quaternion b) => a.x*b.x+a.y*b.y+a.z*b.z+a.w*b.w > .999999f;
    public static bool operator !=(Quaternion a, Quaternion b) => !(a==b);
    public override readonly bool Equals(object other) => other is Quaternion q && x.Equals(q.x) && y.Equals(q.y) && z.Equals(q.z) && w.Equals(q.w);
    public override readonly int GetHashCode() => HashCode.Combine(x,y,z,w);
    public static Quaternion identity=>new(0,0,0,1);
    public readonly N.Quaternion ToNumerics()=>new(x,y,z,w);
    public static Quaternion FromNumerics(N.Quaternion q)=>new(q.X,q.Y,q.Z,q.W);
    public static Quaternion Euler(float x,float y,float z)=>FromNumerics(N.Quaternion.CreateFromYawPitchRoll(y*Mathf.Deg2Rad,x*Mathf.Deg2Rad,z*Mathf.Deg2Rad));
    public static Quaternion AngleAxis(float degrees,Vector3 axis)=>FromNumerics(N.Quaternion.CreateFromAxisAngle(axis.normalized.ToNumerics(),degrees*Mathf.Deg2Rad));
    public static Quaternion Inverse(Quaternion q)=>FromNumerics(N.Quaternion.Inverse(q.ToNumerics()));
    public static Quaternion operator *(Quaternion a, Quaternion b)=>FromNumerics(a.ToNumerics()*b.ToNumerics());
    public static Vector3 operator *(Quaternion a, Vector3 b)=>Vector3.FromNumerics(N.Vector3.Transform(b.ToNumerics(),a.ToNumerics()));
}

public struct Color
{
    public float r,g,b,a;
    public Color(float r,float g,float b,float a=1){this.r=r;this.g=g;this.b=b;this.a=a;}
    public static Color white=>new(1,1,1);
}

