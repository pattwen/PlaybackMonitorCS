cbuffer Parameters:register(b0) { float W,H,InputKind,Matrix; float FullRange,Bits,Preview,Transfer; float RoiX,RoiY,RoiW,RoiH; float HdrBlack,SdrBlack,ViewField,Padding; float FreezePixelDifference,FreezeFlags,FreezeUnusedA,FreezeUnusedB; };
Texture2D<float4> Image:register(t0);
Texture2D<float2> Chroma:register(t1);
SamplerState LinearClamp:register(s0);
struct Vertex {float4 position:SV_POSITION;float2 uv:TEXCOORD0;};
Vertex VSMain(uint id:SV_VertexID) {Vertex v;v.uv=float2((id<<1)&2,id&2);v.position=float4(v.uv*float2(2,-2)+float2(-1,1),0,1);return v;}
float2 ReadChroma(float2 uv)
{
    uint flags=(uint)Padding,location=flags>>2;
    float offsetX=location==1||location==3||location==5?.5/W:0;
    if((flags&1u)==0)
    {
        float offsetY=location==3||location==4?.5/H:location==5||location==6?-.5/H:0;
        return Chroma.SampleLevel(LinearClamp,uv+float2(offsetX,offsetY),0).rg;
    }
    // Chroma rows of 4:2:0 interlaced video belong to alternate fields.
    // Interpolate within the same field; never blend top and bottom field chroma.
    uint cw=0u,ch=0u;Chroma.GetDimensions(cw,ch);
    uint row=min((uint)(uv.y*H),(uint)H-1),field=row&1u;
    float fieldRow=floor(row*.5),centre=location==3||location==4?0:location==5||location==6?1:.5;
    float sampleRow=(fieldRow-centre)*.5,base=floor(sampleRow),fraction=frac(sampleRow);
    float maximum=floor(((float)ch-1-field)*.5)*2+field;
    float r0=clamp(base*2+field,(float)field,maximum),r1=clamp((base+1)*2+field,(float)field,maximum);
    float2 a=Chroma.SampleLevel(LinearClamp,float2(uv.x+offsetX,(r0+.5)/ch),0).rg;
    float2 b=Chroma.SampleLevel(LinearClamp,float2(uv.x+offsetX,(r1+.5)/ch),0).rg;
    return lerp(a,b,fraction);
}
float3 ReadSource(float2 uv)
{
    if(InputKind==0)return Image.SampleLevel(LinearClamp,uv,0).rgb;
    float y=Image.SampleLevel(LinearClamp,uv,0).r;
    float2 c=ReadChroma(uv);
    float scale=Bits>8?65535.0/64.0:255.0;
    y*=scale;c*=scale;
    float maximum=Bits>8?1023:255;
    if(FullRange>0){y/=maximum;c=(c-(Bits>8?512:128))/maximum;}
    else {y=(y-(Bits>8?64:16))/(Bits>8?876:219);c=(c-(Bits>8?512:128))/(Bits>8?896:224);}
    float kr=Matrix==1?.2627:Matrix==2?.299:Matrix==3?.212:Matrix==4?.30:.2126;
    float kb=Matrix==1?.0593:Matrix==2?.114:Matrix==3?.087:Matrix==4?.11:.0722;
    float3 rgb=0;rgb.r=y+2*(1-kr)*c.y;rgb.b=y+2*(1-kb)*c.x;rgb.g=(y-kr*rgb.r-kb*rgb.b)/(1-kr-kb);return rgb;
}
float3 PQ(float3 v) {float3 n=pow(max(v,0),1.0/78.84375);return 10000*pow(max(n-.8359375,0)/max(18.8515625-18.6875*n,1e-6),1.0/.1593017578125);}
float3 HLG(float3 v){return float3(v.r<=.5?v.r*v.r/3:(exp((v.r-.55991073)/.17883277)+.28466892)/12,v.g<=.5?v.g*v.g/3:(exp((v.g-.55991073)/.17883277)+.28466892)/12,v.b<=.5?v.b*v.b/3:(exp((v.b-.55991073)/.17883277)+.28466892)/12)*1000;}
float4 PSConvert(Vertex v):SV_TARGET {return float4(ReadSource(v.uv),1);}
float4 PSDisplay(Vertex v):SV_TARGET
{
    float2 uv=v.uv;
    if(ViewField>0)uv.y=(floor(uv.y*H*.5)*2+(ViewField==1?0:1)+.5)/H;
    float3 rgb=Image.SampleLevel(LinearClamp,uv,0).rgb;
    if(Preview>0&&Transfer>0)
    {
        rgb=Transfer==1?PQ(rgb):HLG(rgb);
        rgb=mul(float3x3(1.6605,-.5876,-.0728,-.1246,1.1329,-.0083,-.0182,-.1006,1.1187),rgb)/100;
        rgb=max(rgb,0);rgb=rgb/(1+rgb);rgb=pow(rgb,1/2.2);
    }
    return float4(rgb,1);
}
#ifdef MONITOR_COMPUTE
// Flat UAV layout: 80 floats per tile. 0..71 retain the previous layout;
// 72..79 add local colour/variance/green/colour-gradient statistics.
// Group-shared arrays are addressable in Shader Model 5. Local struct arrays are not.
Texture2D<float4> PreviousImage : register(t2);
Texture2D<float4> ReferenceImage : register(t3);
RWStructuredBuffer<float> Tiles : register(u0);
groupshared float4 Totals[256];
groupshared float4 Temporal[256];
groupshared float4 ColourTotals[256];
groupshared float4 ColourExtras[256];
groupshared float GradientX[256];
groupshared float GradientY[256];
groupshared float RowGradient[16];
groupshared float RowCount[16];
float Luma(float3 rgb)
{
    return dot(rgb, (((uint)Padding) & 2u) != 0u ? float3(.2627,.678,.0593) : float3(.2126,.7152,.0722));
}
[numthreads(16,16,1)]
void CSMain(uint3 group : SV_GroupID, uint3 local : SV_GroupThreadID, uint lane : SV_GroupIndex)
{
    uint w=(uint)W, h=(uint)H, nx=(w+15u)/16u;
    uint2 p=group.xy*16u+local.xy;
    uint step=(((uint)Padding)&1u)!=0u ? 2u : 1u;
    bool valid=p.x<w && p.y<h && (float)p.x>=RoiX && (float)p.y>=RoiY && (float)p.x<RoiX+RoiW && (float)p.y<RoiY+RoiH;
    float y=0, black=0, gx=0, gy=0;
    float4 motion=0;
    float4 colourTotal=0, colourExtra=0;
    uint temporalFlags=(uint)FreezeFlags;
    if(valid)
    {
        float3 rgb=Image.Load(int3((int2)p,0)).rgb;
        y=Luma(rgb);
        if(FreezeUnusedA>0)
        {
            colourTotal=float4(rgb,y*y);
            float green=rgb.g>.18 && rgb.g>rgb.r*1.35 && rgb.g>rgb.b*1.35 && rgb.g-max(rgb.r,rgb.b)>.08 ? 1.0 : 0.0;
            colourExtra=float4(green,0,max(rgb.r,max(rgb.g,rgb.b))-min(rgb.r,min(rgb.g,rgb.b)),dot(rgb,rgb));
        }
        if((temporalFlags&1u)!=0u)
        {
            float3 difference=abs(rgb-PreviousImage.Load(int3((int2)p,0)).rgb);
            motion.x=max(difference.r,max(difference.g,difference.b));
            motion.z=motion.x>FreezePixelDifference?1.0:0.0;
        }
        if((temporalFlags&2u)!=0u)
        {
            float3 difference=abs(rgb-ReferenceImage.Load(int3((int2)p,0)).rgb);
            motion.y=max(difference.r,max(difference.g,difference.b));
            motion.w=motion.y>FreezePixelDifference?1.0:0.0;
        }
        float luminance=Transfer==1 ? Luma(PQ(rgb)) : Transfer==2 ? Luma(HLG(rgb)) : y;
        black=(Transfer>0 ? luminance<=HdrBlack : y<=SdrBlack) ? 1.0 : 0.0;
        if(p.x+1u<w)
        {
            float3 neighbour=Image.Load(int3((int2)(p+uint2(1u,0u)),0)).rgb;
            gx=abs(y-Luma(neighbour));
            if(FreezeUnusedA>0) { float3 delta=abs(rgb-neighbour); colourExtra.y+=max(delta.r,max(delta.g,delta.b)); }
        }
        if(p.y+step<h)
        {
            float3 neighbour=Image.Load(int3((int2)(p+uint2(0u,step)),0)).rgb;
            gy=abs(y-Luma(neighbour));
            if(FreezeUnusedA>0) { float3 delta=abs(rgb-neighbour); colourExtra.y+=max(delta.r,max(delta.g,delta.b)); }
        }
    }
    Totals[lane]=float4(y,black,valid?1.0:0.0,gx+gy);
    Temporal[lane]=motion;
    ColourTotals[lane]=colourTotal;
    ColourExtras[lane]=colourExtra;
    GradientX[lane]=gx;
    GradientY[lane]=gy;
    GroupMemoryBarrierWithGroupSync();
    uint offset=(group.y*nx+group.x)*80u;
    if(lane<16u)
    {
        float sx=0, sy=0, cx=0, cy=0;
        [loop] for(uint k=0u;k<16u;k++)
        {
            float dx=GradientX[k*16u+lane], dy=GradientY[lane*16u+k];
            sx+=dx; sy+=dy;
            cx+=dx>12.0/255.0?1.0:0.0;
            cy+=dy>12.0/255.0?1.0:0.0;
        }
        Tiles[offset+4u+lane]=sx;
        Tiles[offset+36u+lane]=cx;
        RowGradient[lane]=sy;
        RowCount[lane]=cy;
    }
    GroupMemoryBarrierWithGroupSync();
    if(lane<16u)
    {
        float sy=0, cy=0;
        if(step==1u) { sy=RowGradient[lane]; cy=RowCount[lane]; }
        else
        {
            uint first=(group.y*8u)%16u;
            if(lane>=first && lane<first+8u)
            {
                uint row=(lane-first)*2u;
                sy=RowGradient[row]+RowGradient[row+1u];
                cy=RowCount[row]+RowCount[row+1u];
            }
        }
        Tiles[offset+20u+lane]=sy;
        Tiles[offset+52u+lane]=cy;
    }
    [loop] for(uint stride=128u;stride>0u;stride>>=1u)
    {
        if(lane<stride)
        {
            Totals[lane]+=Totals[lane+stride]; Temporal[lane]+=Temporal[lane+stride];
            ColourTotals[lane]+=ColourTotals[lane+stride]; ColourExtras[lane]+=ColourExtras[lane+stride];
        }
        GroupMemoryBarrierWithGroupSync();
    }
    if(lane==0u)
    {
        Tiles[offset]=Totals[0].x;
        Tiles[offset+1u]=Totals[0].y;
        Tiles[offset+2u]=Totals[0].z;
        Tiles[offset+3u]=Totals[0].w;
        Tiles[offset+68u]=Temporal[0].x;
        Tiles[offset+69u]=Temporal[0].y;
        Tiles[offset+70u]=Temporal[0].z;
        Tiles[offset+71u]=Temporal[0].w;
        Tiles[offset+72u]=ColourTotals[0].x;
        Tiles[offset+73u]=ColourTotals[0].y;
        Tiles[offset+74u]=ColourTotals[0].z;
        Tiles[offset+75u]=ColourTotals[0].w;
        Tiles[offset+76u]=ColourExtras[0].x;
        Tiles[offset+77u]=ColourExtras[0].y;
        Tiles[offset+78u]=ColourExtras[0].z;
        Tiles[offset+79u]=ColourExtras[0].w;
    }
}
#endif
