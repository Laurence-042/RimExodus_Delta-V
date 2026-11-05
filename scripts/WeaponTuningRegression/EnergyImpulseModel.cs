using System;

// Proposed rules only. This file is shared by the two offline executables;
// it is never compiled into DeltaV.dll and does not describe current gameplay.
internal static class EnergyImpulseModel
{
    public const double MaximumImpulse = 360;
    public const double ChemicalBreakDeltaV = 4.5, RailBreakDeltaV = .5;
    public const double ChemicalFragmentEfficiency = .25, RailFragmentEfficiency = .02;
    public static double Energy(double mass,double normal,double tangent=0) => .5*mass*(normal*normal+tangent*tangent);
    public static double Budget(double energy,bool rail=false) => energy/(rail?225:75);
    public static double RailEnergy(bool large,int stages) => (large?2:1)*(324000d+216000d*stages);
    public static double Cost(double mass,double normal,double impulse)
    {
        if(impulse<=0)return 0;
        double work=Energy(mass,normal+impulse/mass)-Energy(mass,normal);
        // A fixed processing price for impulse plus positive work supplied by
        // the field. Braking does not refund energy to the grid.
        return 2+16*impulse/MaximumImpulse+Math.Max(0,work)/1800;
    }
    public sealed class Response
    {
        public double Impulse,Cost,Normal,Tangent,Energy,Damage;
        public bool Broken,Reflected,Underfunded;
        public int Fragments;
    }
    public static Response Apply(double mass,double normal,double tangent,bool rail,int strength,double available=double.PositiveInfinity,bool fragmented=false)
    {
        if(mass<=0)throw new ArgumentOutOfRangeException(nameof(mass));
        double wanted=MaximumImpulse*Math.Clamp(strength,0,100)/100,impulse=wanted;
        if(normal>=0)wanted=impulse=0; // backside or parallel rays do not trigger
        double desiredCost=Cost(mass,normal,wanted);
        if(available<desiredCost)
        {
            if(available<=2)impulse=0;
            else
            {
                double lo=0,hi=wanted;
                for(int i=0;i<56;i++)
                {double mid=(lo+hi)/2;if(Cost(mass,normal,mid)<=available)lo=mid;else hi=mid;}
                impulse=lo;
            }
        }
        double next=normal+impulse/mass;
        bool broken=!fragmented&&impulse>0&&impulse/mass+1e-10>=(rail?RailBreakDeltaV:ChemicalBreakDeltaV);
        double energy=Energy(mass,next,tangent);
        return new Response{Impulse=impulse,Cost=Cost(mass,normal,impulse),Normal=next,Tangent=tangent,
            Energy=energy,Damage=Budget(energy,rail)*(broken||fragmented?(rail?RailFragmentEfficiency:ChemicalFragmentEfficiency):1),
            Broken=broken,Reflected=next>=0,Fragments=broken?(rail?4:3):1,Underfunded=available+1e-8<desiredCost};
    }
}
