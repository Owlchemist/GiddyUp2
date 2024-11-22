using System.Collections.Generic;
using Verse;

namespace GiddyUp
{
    class FactionRestrictions : DefModExtension
    {
        //Can be used in xml patches to restrict animals per faction. 
        public List<PawnKindDef> allowedNonWildAnimals = new List<PawnKindDef>();
        public List<PawnKindDef> allowedWildAnimals = new List<PawnKindDef>();
        public int mountChance = -1;
        public int wildAnimalWeight = -1;
        public int nonWildAnimalWeight = -1;
    }
}