using System.Collections.Generic;
using Verse;

namespace GiddyUp
{
    //For ThingDef
    class ResearchRestrictions : DefModExtension
    {
        public ResearchProjectDef researchProjectDefToAttack = null;
        public List<ResearchProjectDef> researchProjectDefs = new List<ResearchProjectDef>();
    }
}