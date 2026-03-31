#!/usr/bin/env python3
"""
Dictionary Curation v2 — Frequency-anchored approach
=====================================================
Instead of trying to detect obscure words (hard), we:
1. Start with a curated list of ~8,000 common English words as the CORE anchor
2. Extended = Core + words that look structurally normal
3. Full = everything (enable1)

The Core list is built by:
- Taking all common English words (manually curated high-frequency set)
- Adding their standard inflections (-S, -ED, -ING, -ER, -EST, -LY)
- Removing anything not in enable1 (so we don't add invalid words)
"""

import os
import re

SCRIPT_DIR = os.path.dirname(os.path.abspath(__file__))
FULL_PATH = os.path.join(SCRIPT_DIR, "dict_full.txt")
CORE_PATH = os.path.join(SCRIPT_DIR, "dict_core.txt")
EXTENDED_PATH = os.path.join(SCRIPT_DIR, "dict_extended.txt")
REMOVED_CORE_PATH = os.path.join(SCRIPT_DIR, "dict_removed_from_core.txt")
REMOVED_EXT_PATH = os.path.join(SCRIPT_DIR, "dict_removed_from_extended.txt")
FORCE_INCLUDE_PATH = os.path.join(SCRIPT_DIR, "dict_force_include.txt")
FORCE_EXCLUDE_PATH = os.path.join(SCRIPT_DIR, "dict_force_exclude.txt")

# Load full word list
with open(FULL_PATH) as f:
    full_set = set(line.strip().upper() for line in f if line.strip())
full_list = sorted(full_set)
print(f"Full dictionary: {len(full_list)} words")

# ═══════════════════════════════════════════════════════════════════════════════
# STAGE 1: Build Core dictionary from common word roots + inflections
# ═══════════════════════════════════════════════════════════════════════════════

# These are common English ROOT words that a normal person would recognize.
# We then auto-expand with standard inflections.
# This is NOT exhaustive — it's the anchor. Words not here can still make
# Extended via the structural filter.

COMMON_ROOTS = """
ace act add age ago aid aim air all and ant any ape apt arc are ark arm art
ash ask ate awe axe aye
back bad bag bake ball ban band bang bank bar bare barn base bash bat bath bay
bead beam bean bear beat bed bee beef been beer bell belt bench bend bent best
bet bid big bike bill bin bind bird bit bite black blade blame blank blast blaze
bleak bleed blend bless blind blink bliss block blood bloom blow blue blur board
boast boat body boil bold bolt bomb bond bone book boom boost boot bore born
boss both bound bow bowl box boy brain brake brand brave bread break breath
breed brick bride brief bright bring broad broke bronze brood brook brown brush
buck bud buddy bug build bulk bull bump bunch burn burst bury bus bush busy but
buy buzz
cab cage cake call calm came camp can cap cape car card care cart case cash cast
cat catch cause cave cell chain chair chalk champ chance change chant charge
charm chart chase cheap cheat check cheek cheer chest chew chief child chill
chin chip choice choke choose chop chunk church cite city claim clam clamp clap
clash clasp class claw clay clean clear clerk click cliff climb cling clip cloak
clock clone close cloth cloud clown club clue clump clung coach coal coast coat
code coil coin cold collar color colt comb come comfort comic common company
cone cook cool cope copy cord core cork corn corner cost couch could count
counter couple courage course court cousin cover cow crack craft cramp crane
crash crate crave crawl crazy cream create creek crew crime crisp crop cross
crowd crown crush cry crystal cub cube cue cup curb cure curl curse curve
cushion custom cut cute cycle
dab dad daily damage damp dance dare dark darn dart dash data date dawn day dead
deaf deal dear death debate debt decay deck declare decline decor deep deer
defeat delay demand demon dent deny depth desert design desire desk detail devil
devote dial dice did die diet dig dim dine dinner dip dire dirt dirty dish disk
dive dock doctor dodge dog doll dome done doom door dose dot double doubt dough
dove down draft drag drain drama drank drape draw drawer dream dress drew dried
drift drill drink drip drive drop drove drown drum drunk dry duck dude due duel
dug dull dumb dump dune dunk dusk dust duty dye dying
each eagle ear earn earth ease east easy eat echo edge edit egg ego eight either
elbow elder elect elf elm else emerge emit empire empty end enemy energy engine
enjoy enough enter entire entry equal era erase error escape essay even evening
event ever every evil exact exam exceed excel except excite excuse exit expand
expect expert explain explore export expose extend extra eye
fable face fact fade fail faint fair faith fake fall false fame family fan fancy
fang far fare farm fast fat fate father fault favor fawn fear feast feat feather
fed fee feed feel feet fell fellow felt fence ferry fever few fiber fiction
field fierce fight figure file fill film final find fine finger finish fire firm
first fish fist fit five fix flag flame flap flash flat flaw fled flee flesh
flew flight fling flip float flock flood floor flop flour flow flower flu fluff
fluid flush flute fly foam focus fog foil fold folk follow fond food fool foot
for force forest forever forget forgive fork form fort fortune found four fox
frame free freeze fresh friend fright frog from front frost frown froze fruit
fuel full fun fund funny fur fury fuse fuss future
gag gain gale game gang gap garage garden garlic gas gasp gate gather gave gaze
gear gem general gentle get ghost giant gift gig gild girl give given glad
glance glare glass gleam glide glimpse globe gloom glory gloss glove glow glue
goal goat god gold golf gone good goose gore got govern grab grace grade grain
grand grant grape grasp grass grave gray graze great greed green greet grew grey
grief grill grim grin grind grip groan groom ground group grove grow growth
guard guess guest guide guilt guitar gulf gum gun gust gut guy gym
habit hack hail hair half hall halt hammer hand handle hang happen happy harbor
hard harm harp harsh harvest has haste hat hate haul haunt have hawk hay haze
hazy head heal health heap hear heart heat heavy hedge heel held hell hello help
hem hen her herb herd here hero hid hide high hike hill him hind hint hip hire
his hit hive hog hold hole hollow holy home honest honor hood hook hope horizon
horn horror horse host hostile hot hotel hound hour house hover how howl hub hue
hug huge human humble humor hundred hung hunger hunt hurl hurry hurt hush hut
ice icy idea ideal idle ill image imagine impact import impose impress improve
inch include income increase index infant inform initial inject inner input
insect inside insist instead insult intend interest into invite involve ion iron
island issue item ivory ivy
jab jag jail jam jar jaw jazz jeans jeer jelly jerk jet jewel jig job jog join
joke jolt journal journey joy judge jug juice jump jungle junior jury just
keen keep keg ken kept key kick kid kill kind king kiss kit kitchen kite kitten
knack knee kneel knew knife knight knit knob knock knot know
lab lace lack lad laden lady lag laid lake lamb lame lamp land lane lap large
lark lash lass last latch late later laugh launch lava law lawn lawyer lay layer
lazy lead leaf league leak lean leap learn lease leash least leave led left leg
legend lemon lend length lens less lesson let letter level lever liberty library
lick lid lie life lift light like lily limb lime limit limp line linger link
lion lip liquid list listen lit litter little live liver load loaf loan lobby
local lock lodge log lone long look loop loose lord lose loss lost lot loud
lounge love lovely lover low loyal luck lump lunch lure lurk lush
machine mad made magic maid mail main major make male mall malt man manage mane
manner manor many map maple march mare margin mark market marry marsh mask mass
mast master match mate matter mature may mayor maze meadow meal mean measure
meat medal media meet melt member memo memory men mend mental mention mentor
menu mercy mere merge merit merry mesh mess metal method middle might mild mile
milk mill mimic mind mine minor mint minute mirror mist mix moan moat mob mock
mode modern modest moist mold mole moment money monk monkey mood moon moral more
morning moss most motel mother motion motor mount mouse mouth move movie much
muck mud mug mule mumble murder muscle museum music must mute mutual mystery
myth
nab nag nail name nap napkin narrow nasty native nature navy near neat neck need
nerve nest net never new news next nice nick night nine noble nod noise none
noon norm normal north nose notable note nothing notice novel now nowhere nude
number nurse nut
oak oar oat obey object observe obtain obvious ocean odd odds offer office
often oil okay old olive once one only onto open opera opinion oppose option
orange orbit order organ other ought ounce our outer output outside oven over
owe owl own owner
pace pack pad page paid pail pain paint pair palace pale palm pan pane panel
panic pant paper parade parent park part partner party pass passage past paste
patch path pattern pause pave pay peace peach peak pear pearl peck peek peel
peer pen pencil penny people pepper per perfect period permit person pest pet
phase phone photo phrase pick picture pie piece pig pile pill pilot pin pinch
pine pink pipe pit pitch pity place plain plan plane plant plate play player
plea plead please pledge plenty plot plow pluck plug plum plumb plump plunge
plus pocket poem poet point poison pole police polish polite poll pond pool poor
pop popular porch port portion pose position post pot potato pound pour poverty
powder power praise pray prayer press pretty prevent price pride prime prince
print prior prison private prize problem produce profit program project promise
promote prompt proof proper protect protest proud prove provide public pull pulp
pulse pump punch pupil puppet purchase pure purple purpose purse push put puzzle
quarter queen question quick quiet quilt quit quite quiz quote
race rack radar rage raid rail rain raise ramp ranch random range rank rapid
rare rash rate rather raw ray reach react read ready real reason rebel recall
receive recent record recover red reduce reef reel refer reflect reform refuse
region regret reject relate release relief rely remain remark remedy remember
remote remove rent repair repeat replace report request require rescue reserve
resist resolve resort resource respect respond rest restore result retain retire
return reveal reverse review revolt rhythm rib ribbon rice rich rid ride ridge
rifle right rigid rim ring rinse riot ripe rise risk rival river road roam roar
rob robe robin rock rod rode role roll romance roof room root rope rose rot
rough round route row royal rub rude rug ruin rule rumor run rush rust rut
sack sacred sad safe said sail saint sake sale salt same sand sang sank sap sat
sauce save saw say scale scan scar scene scent school science score scout scrap
scratch scream screen script sea seal search season seat second secret section
secure see seed seek seem seen seize select self sell send senior sense sent
separate series serious serve service session set settle seven severe shade
shadow shaft shake shall shame shape share sharp shatter shave she shed sheer
sheet shelf shell shelter shift shine ship shirt shock shoe shoot shop shore
short shot should shoulder shout shove show shower shut shy sick side sigh sight
sign signal silence silent silk silly silver similar simple since sing single
sink sir sister sit site six size ski skill skin skip skirt skull sky slab slam
slap slate slave sleep sleeve slender slice slick slide slim sling slip slit
slope slow slug slum smart smash smell smile smoke smooth snap snatch sneak snow
soak soap soar social sock soft soil solar sold soldier solid solve some son
song soon sort soul sound soup source south space spare spark speak special
speech speed spell spend sphere spice spider spike spin spirit splash split
spoke spoon sport spot spread spring squad square squeeze stable staff stage
stain stair stake stale stall stamp stand stare start state station status stay
steady steak steal steam steel steep steer stem step stick stiff still sting
stir stock stone stood stool stop store storm story stout stove straight
strange strap straw stream street stress stretch strict stride strike string
strip stripe stroke strong struck structure struggle stuck student study stuff
stumble style subject submit subtle succeed such sudden suffer sugar suggest
suit summer summit sun super supper supply support suppose sure surface surge
surprise survive suspect suspend swallow swamp swap swarm sway swear sweat
sweep sweet swell swept swift swim swing switch sword swore symbol system
table tackle tail take tale talent talk tall tame tan tank tap tape target task
taste taught tax tea teach team tear teeth tell temper temple ten tend tennis
tense tent term terror test text than thank that the their them theme then there
thick thief thin thing think third this thorn those though thought thousand
thread threat three threw thrill thrive throat throne through throw thrust
thumb thunder tick ticket tide tidy tie tiger tight till timber time tin tiny
tip tire title toast today toe together toll tomb tomorrow tone tongue tonight
too tool tooth top topic torch tore torn total touch tough tour toward tower
town trace track trade trail train trait trap trash travel tray treasure treat
tree trend trial tribe trick tried trigger trim trio trip triumph trod troop
trophy trouble trout truck true truly trumpet trunk trust truth try tube tuck
tuesday tumor tune tunnel turkey turn turtle twelve twenty twice twin twist two
type
ugly uncle under unfold uniform union unique unit unite universe until update
upon upper upset urban urge us use used useful user usual
vacant valley value van vanish vapor vary vase vast veil vein velvet venture
verb version very vessel veteran vibrate vice victim video view village vine
violet virtue visible vision visit visual vital vivid voice void volume vote vow
voyage
wade wage wagon wait wake walk wall wander want war ward warm warn warp warrior
wash waste watch water wave wax way weak wealth weapon wear weather web wedding
weed week weigh welcome well west western wet whale what wheat wheel when where
which while whip whisper white who whole whom whose wicked wide wife wild will
win wind window wine wing winner winter wire wisdom wise wish wit witch within
without witness woke wolf woman wonder wood wool word wore work world worm worry
worse worship worst worth would wound wrap wrist write wrong wrote
yacht yank yard yarn yawn year yell yellow yes yesterday yet yield you young
your youth
zeal zero zest zinc zone zoo zoom
""".split()

# De-dup and uppercase
COMMON_ROOTS = sorted(set(w.upper() for w in COMMON_ROOTS))
print(f"Common root words: {len(COMMON_ROOTS)}")

# ─── Generate inflections ────────────────────────────────────────────────────

def generate_inflections(root):
    """Generate standard English inflections of a root word."""
    forms = {root}
    r = root.upper()
    rlen = len(r)

    # Skip if already at max length for inflections
    if rlen >= 7:
        return forms

    # -S plural / 3rd person
    if rlen <= 6:
        if r.endswith(("S", "X", "Z", "CH", "SH")):
            forms.add(r + "ES")
        elif r.endswith("Y") and rlen > 1 and r[-2] not in "AEIOU":
            forms.add(r[:-1] + "IES")
        else:
            forms.add(r + "S")

    # -ED past tense
    if rlen <= 5:
        if r.endswith("E"):
            forms.add(r + "D")
        elif r.endswith("Y") and rlen > 1 and r[-2] not in "AEIOU":
            forms.add(r[:-1] + "IED")
        elif rlen >= 3 and r[-1] not in "AEIOUWXY" and r[-2] in "AEIOU" and r[-3] not in "AEIOU":
            # CVC pattern — double final consonant
            forms.add(r + r[-1] + "ED")
        else:
            forms.add(r + "ED")

    # -ING
    if rlen <= 4:
        if r.endswith("E") and not r.endswith("EE"):
            forms.add(r[:-1] + "ING")
        elif rlen >= 3 and r[-1] not in "AEIOUWXY" and r[-2] in "AEIOU" and r[-3] not in "AEIOU":
            forms.add(r + r[-1] + "ING")
        else:
            forms.add(r + "ING")

    # -ER comparative / agent noun
    if rlen <= 5:
        if r.endswith("E"):
            forms.add(r + "R")
        elif r.endswith("Y") and rlen > 1 and r[-2] not in "AEIOU":
            forms.add(r[:-1] + "IER")
        else:
            forms.add(r + "ER")

    # -EST superlative
    if rlen <= 4:
        if r.endswith("E"):
            forms.add(r + "ST")
        elif r.endswith("Y") and rlen > 1 and r[-2] not in "AEIOU":
            forms.add(r[:-1] + "IEST")
        else:
            forms.add(r + "EST")

    # -LY adverb
    if rlen <= 5:
        if r.endswith("Y"):
            forms.add(r[:-1] + "ILY")
        elif r.endswith("LE"):
            forms.add(r[:-1] + "Y")
        else:
            forms.add(r + "LY")

    # -NESS
    if rlen <= 4:
        if r.endswith("Y") and rlen > 1 and r[-2] not in "AEIOU":
            forms.add(r[:-1] + "INESS")
        else:
            forms.add(r + "NESS")

    # -MENT
    if rlen <= 4:
        forms.add(r + "MENT")

    # -ABLE
    if rlen <= 3:
        if r.endswith("E"):
            forms.add(r[:-1] + "ABLE")
        else:
            forms.add(r + "ABLE")

    # Only keep forms that are 3-7 letters
    return {f for f in forms if 3 <= len(f) <= 7}


# Generate all forms
core_from_roots = set()
for root in COMMON_ROOTS:
    if root in full_set:
        core_from_roots.add(root)
    forms = generate_inflections(root)
    for form in forms:
        if form in full_set:
            core_from_roots.add(form)

print(f"Core from roots + inflections: {len(core_from_roots)} (validated against enable1)")

# ═══════════════════════════════════════════════════════════════════════════════
# STAGE 2: Expand Core with structurally normal words from enable1
# ═══════════════════════════════════════════════════════════════════════════════

# Many perfectly good words aren't in our root list. We add words that:
# 1. Have normal English structure (no exotic letter combos)
# 2. Are common enough to feel fair
# 3. Don't match obscure patterns

def is_structurally_normal(word):
    """Returns True if word looks like a normal English word."""
    w = word.upper()

    # Must have at least one vowel
    if not any(c in w for c in "AEIOUY"):
        return False

    # Q without QU is almost always obscure
    if "Q" in w and "QU" not in w:
        return False

    # Exotic double vowels
    if w.startswith("AA") or "II" in w or "UU" in w:
        return False

    # Exotic starts
    exotic_starts = {"BH", "BW", "CW", "DH", "DZ", "FJ", "GH", "HM",
                     "KH", "MH", "NH", "NJ", "PF", "SR", "SV", "VL",
                     "ZH", "ZW", "ZY"}
    if len(w) >= 2 and w[:2] in exotic_starts:
        return False

    # Quad consonant cluster
    consonants = "BCDFGHJKLMNPQRSTVWXZ"
    count = 0
    for c in w:
        if c in consonants:
            count += 1
            if count >= 4:
                return False
        else:
            count = 0

    return True


# Patterns that strongly signal obscure words (for removal from Extended+)
OBSCURE_WORD_PATTERNS = re.compile(
    r"(AALII|AARRG|ABMHO|ABVOLT|ABWATT|ACNODE|ADNOUN|AECIDI|AEOLIAN|"
    r"AGAPE[IS]|AGAROS|AGOUTY|AHIMSA|AIOLI|AMEBAE|ANKUSH|ASCI|"
    r"ATLATL|AZIDO|BHAJI|BOYAR|BURET[TE]|BYSSI|CAIRD|CALX|CAPH|"
    r"COYPU|CRWTH|CWMS|DHIKR|DHOBI|DHOTI|DJINN|DUROC|EDDO|"
    r"ELHI|ENOKI|ERGOT|ESKAR|EYRA|FROE|FYTTE|GADDI|GALAX|GALUT|"
    r"GHAZI|GHEE|GLOGG|GLYPH|GUANA|GYOZA|HAJJ|HAPAX|HIJAB|HIJRA|"
    r"HOKUM|ICTUS|IHRAM|JIAO|JHEEL|JOWAR|KABAR|KANZU|KARST|"
    r"KEDGE|KEFIR|KENDO|KNISH|KOPEK|KRAAL|KUKRI|KYRIE|LAARI|"
    r"MANAT|MBIRA|MHORR|MILCH|MIRZA|MIZEN|MOREL|MUDIR|NAEVI|"
    r"NGOMA|NIQAB|NJORD|NOYAU|NUBIA|OKAPI|ORIBI|PAEON|PAISE|"
    r"PIETA|PILAF|PIROG|PLAYA|PRAHU|QANAT|QINTAR|RAJAH|RAYAH|"
    r"RIYAL|SAICE|SARIN|SERAI|SHOJI|SIGIL|SOLDI|STELE|SURAH|"
    r"TALAR|THANE|TIKKA|TORII|TSUBA|UHLAN|UKASE|UMAMI|URIAL|"
    r"VEENA|WAQF|WASABI|XERIC|YENTA|ZAYIN|ZIRAM|ZLOTY|ZOEAE|ZYMUR)"
)

# ─── Obscure 3-letter words to EXCLUDE from all tiers except Full ────────────
# These are the Scrabble-specialist words that no casual player knows
OBSCURE_3 = {
    "AAH", "AAL", "AAS", "ABA", "ABO", "ABY", "AGA", "AGS", "AHI", "AHS",
    "AIN", "AIS", "AIT", "ALA", "ALB", "ALS", "AMA", "AMI", "AMU", "ANA",
    "ANE", "ANI", "APO", "ARB", "ARD", "ARF", "ARS", "ATT", "AUK", "AVA",
    "AVO", "AWA", "AWN", "AYS", "AZO",
    "BAH", "BAP", "BAS", "BEL", "BEN", "BES", "BEY", "BIS", "BOD", "BOS",
    "BOT", "BRR", "BUB", "BUR", "BYS",
    "CAW", "CEE", "CEL", "CEP", "CHI", "CIG", "CIS", "COG", "COL", "COR",
    "COS", "COZ", "CRU", "CWM",
    "DAG", "DAH", "DAK", "DAL", "DAP", "DAW", "DEL", "DEV", "DEX", "DEY",
    "DIB", "DIS", "DIT", "DOL", "DOP", "DOR", "DOS", "DOW", "DUI", "DUP",
    "EAU", "ELD", "ELL", "ELS", "EME", "EMS", "ENG", "ENS", "ERE", "ERG",
    "ERN", "ERR", "ERS", "ESS", "ETA", "ETH", "EWE",
    "FAG", "FAY", "FEH", "FEM", "FER", "FES", "FET", "FEU", "FEY", "FID",
    "FIE", "FIR", "FIS", "FOB", "FOH", "FON", "FOP", "FOU", "FOY", "FRO",
    "FUB", "FUD", "FUG",
    "GAB", "GAE", "GAM", "GAN", "GAR", "GAT", "GED", "GEE", "GEY", "GHI",
    "GIB", "GID", "GIE", "GIP", "GIT", "GOA", "GOB", "GOO", "GOR", "GOS",
    "GOX", "GUL", "GUP", "GUS", "GUV", "GYP",
    "HAE", "HAH", "HAJ", "HAO", "HAP", "HEH", "HEP", "HES", "HET", "HIC",
    "HIE", "HIN", "HMM", "HOD", "HOY", "HUP",
    "ICH", "IDS", "IFF", "IGG", "ILK", "INS",
    "JEE", "JIB", "JIN", "JOE", "JOW", "JUS", "JUT",
    "KAB", "KAE", "KAF", "KAS", "KAT", "KAY", "KEA", "KED", "KEF", "KEP",
    "KEX", "KHI", "KIF", "KIP", "KIR", "KIS", "KOA", "KOB", "KOI", "KOP",
    "KOR", "KOS", "KUE",
    "LAC", "LAM", "LAS", "LAT", "LAV", "LEA", "LEI", "LEK", "LES", "LEU",
    "LEV", "LEX", "LEY", "LIS",
    "MAE", "MAG", "MAS", "MAW", "MEL", "MEM", "MET", "MEW", "MHO", "MIB",
    "MIG", "MIL", "MIM", "MIR", "MIS", "MOC", "MOG", "MOL", "MOR", "MOS",
    "MUN", "MUS", "MUT", "MUX",
    "NAE", "NAH", "NAM", "NAW", "NEE", "NIB", "NIM", "NIX", "NOB", "NOG",
    "NOM", "NOO", "NOR", "NOS", "NUB", "NUS",
    "OBE", "OBI", "OCA", "ODD", "ODS", "OES", "OHO", "OHS", "OKA", "OKE",
    "OLE", "OMS", "ONO", "ONS", "OOH", "OOT", "OOZ", "OPE", "OPS", "ORA",
    "ORC", "ORS", "ORT", "OSE", "OUD", "OVA", "OXO", "OXY",
    "PAC", "PAH", "PAM", "PAS", "PAX", "PEC", "PED", "PEH", "PEP", "PES",
    "PHI", "PHT", "PIA", "PIS", "PIU", "PIX", "POD", "POH", "POI", "POL",
    "POM", "POW", "PSI", "PUL", "PUR", "PUS",
    "QAT", "QIS", "QUA",
    "RAH", "RAI", "RAJ", "RAS", "RAX", "REB", "REC", "REE", "REG", "REI",
    "REM", "REP", "RES", "RET", "REV", "RHO", "RIA", "RIF", "RIN", "ROC",
    "ROM",
    "SAB", "SAC", "SAE", "SAU", "SEG", "SEI", "SEL", "SEN", "SER", "SHA",
    "SHH", "SIB", "SIC", "SIM", "SKA", "SOL", "SOM", "SOP", "SOS", "SOT",
    "SOU", "SOX", "SUD", "SUQ", "SYN",
    "TAD", "TAE", "TAJ", "TAM", "TAO", "TAS", "TAT", "TAU", "TAV", "TAW",
    "TEG", "TET", "TEW", "THO", "TIS", "TOD", "TOG", "TOR", "TSK", "TUI",
    "TUN", "TUP", "TUT", "TWA",
    "UDO", "UDS", "UGH", "UKE", "ULU", "UMM", "UNS", "UPO", "UPS", "URB",
    "URD", "URP", "URS", "UTA", "UTE", "UTS",
    "VAC", "VAR", "VAS", "VAU", "VAV", "VAW", "VEE", "VID", "VIE", "VIG",
    "VIS", "VUG",
    "WAB", "WAD", "WAE", "WAP", "WAT", "WAW", "WEN", "WHA", "WIS", "WOG",
    "WOP", "WOS", "WOT",
    "YAH", "YAR", "YAW", "YEA", "YEH", "YEW", "YID", "YIN", "YIP", "YOB",
    "YOD", "YOK", "YOM", "YON", "YOW", "YUK",
    "ZAG", "ZAX", "ZED", "ZEE", "ZEK", "ZEP", "ZIN",
}

# Common 3-letter words that MUST stay
KEEP_3 = {
    "ACE", "ACT", "ADD", "ADO", "ADS", "AFT", "AGE", "AGO", "AID", "AIM",
    "AIR", "ALE", "ALL", "AND", "ANT", "ANY", "APE", "APP", "ARC", "ARE",
    "ARK", "ARM", "ART", "ASH", "ASK", "ASP", "ATE", "AWE", "AXE", "AYE",
    "BAD", "BAG", "BAM", "BAN", "BAR", "BAT", "BAY", "BED", "BEE", "BEG",
    "BET", "BIB", "BIG", "BIN", "BIT", "BIZ", "BOB", "BOG", "BOP", "BOW",
    "BOX", "BOY", "BRA", "BRO", "BUD", "BUG", "BUM", "BUN", "BUS", "BUT",
    "BUY", "BYE",
    "CAB", "CAD", "CAM", "CAN", "CAP", "CAR", "CAT", "COB", "COD", "CON",
    "COO", "COP", "COT", "COW", "COX", "COY", "CRY", "CUB", "CUD", "CUE",
    "CUP", "CUR", "CUT",
    "DAB", "DAD", "DAM", "DAY", "DEB", "DEE", "DEN", "DEW", "DID", "DIE",
    "DIG", "DIM", "DIN", "DIP", "DOC", "DOE", "DOG", "DON", "DOT", "DRY",
    "DUB", "DUD", "DUE", "DUG", "DUH", "DUN", "DUO", "DYE",
    "EAR", "EAT", "EEL", "EEK", "EGG", "EGO", "EKE", "ELF", "ELK", "ELM",
    "EMU", "END", "EON", "ERA", "EVE", "EWE", "EYE",
    "FAB", "FAD", "FAN", "FAR", "FAT", "FAX", "FED", "FEE", "FEN", "FEW",
    "FIB", "FIG", "FIN", "FIT", "FIX", "FLU", "FLY", "FOE", "FOG", "FOR",
    "FOX", "FRY", "FUN", "FUR",
    "GAG", "GAL", "GAP", "GAS", "GAY", "GEL", "GEM", "GET", "GIG", "GIN",
    "GNU", "GOD", "GOT", "GUM", "GUN", "GUT", "GUY", "GYM",
    "HAD", "HAM", "HAS", "HAT", "HAW", "HAY", "HEN", "HER", "HEW", "HEX",
    "HEY", "HID", "HIM", "HIP", "HIS", "HIT", "HOB", "HOG", "HOP", "HOT",
    "HOW", "HUB", "HUE", "HUG", "HUH", "HUM", "HUN", "HUT",
    "ICE", "ICK", "ICY", "ILL", "IMP", "INK", "INN", "ION", "IRE", "IRK",
    "ISM", "ITS", "IVY",
    "JAB", "JAG", "JAM", "JAR", "JAW", "JAY", "JET", "JIG", "JOB", "JOG",
    "JOT", "JOY", "JUG",
    "KEG", "KEN", "KEY", "KID", "KIN", "KIT",
    "LAB", "LAD", "LAG", "LAP", "LAW", "LAX", "LAY", "LED", "LEE", "LEG",
    "LET", "LID", "LIE", "LIP", "LIT", "LOG", "LOO", "LOP", "LOT", "LOW",
    "LUG",
    "MAD", "MAN", "MAP", "MAR", "MAT", "MAX", "MAY", "MED", "MEN", "MIC",
    "MID", "MIX", "MOB", "MOD", "MOM", "MOO", "MOP", "MOT", "MOW", "MUD",
    "MUG", "MUM",
    "NAB", "NAG", "NAP", "NAY", "NET", "NEW", "NIL", "NIP", "NIT", "NOD",
    "NOR", "NOT", "NOW", "NUN", "NUT",
    "OAF", "OAK", "OAR", "OAT", "ODD", "ODE", "OFF", "OFT", "OHM", "OIL",
    "OLD", "ONE", "OPT", "ORB", "ORE", "OUR", "OUT", "OWE", "OWL", "OWN",
    "PAD", "PAL", "PAN", "PAP", "PAR", "PAT", "PAY", "PEA", "PEE", "PEG",
    "PEN", "PER", "PET", "PEW", "PIE", "PIG", "PIN", "PIP", "PIT", "PLY",
    "POP", "POT", "POX", "PRO", "PRY", "PUB", "PUD", "PUG", "PUN", "PUP",
    "PUT",
    "RAD", "RAG", "RAM", "RAN", "RAP", "RAT", "RAW", "RAY", "RED", "REF",
    "RIB", "RID", "RIG", "RIM", "RIP", "ROB", "ROD", "ROE", "ROT", "ROW",
    "RUB", "RUE", "RUG", "RUM", "RUN", "RUT", "RYE",
    "SAD", "SAG", "SAP", "SAT", "SAW", "SAX", "SAY", "SEA", "SEC", "SEE",
    "SET", "SEW", "SHE", "SHY", "SIN", "SIP", "SIR", "SIS", "SIT", "SIX",
    "SKI", "SKY", "SLY", "SOB", "SOD", "SON", "SOW", "SOY", "SPA", "SPY",
    "STY", "SUB", "SUE", "SUM", "SUN", "SUP",
    "TAB", "TAG", "TAN", "TAP", "TAR", "TAX", "TEA", "TEN", "THE", "THY",
    "TIC", "TIE", "TIN", "TIP", "TIT", "TOE", "TOM", "TON", "TOO", "TOP",
    "TOT", "TOW", "TOY", "TUB", "TUG", "TUX", "TWO",
    "URN", "USE",
    "VAN", "VAT", "VEG", "VET", "VEX", "VIA", "VIM", "VOW",
    "WAD", "WAG", "WAR", "WAS", "WAX", "WAY", "WEB", "WED", "WEE", "WET",
    "WHO", "WHY", "WIG", "WIN", "WIT", "WIZ", "WOE", "WOK", "WON", "WOO",
    "WOW",
    "YAK", "YAM", "YAP", "YAY", "YEN", "YEP", "YES", "YET", "YOU", "YUM",
    "YUP",
    "ZAP", "ZEN", "ZIP", "ZIT", "ZOO",
}

# ═══════════════════════════════════════════════════════════════════════════════
# STAGE 3: Build final tiers
# ═══════════════════════════════════════════════════════════════════════════════

# Load force include/exclude if they exist
force_include = set()
force_exclude = set()
if os.path.exists(FORCE_INCLUDE_PATH):
    with open(FORCE_INCLUDE_PATH) as f:
        for line in f:
            w = line.strip().upper()
            if w and not w.startswith("#"):
                force_include.add(w)
if os.path.exists(FORCE_EXCLUDE_PATH):
    with open(FORCE_EXCLUDE_PATH) as f:
        for line in f:
            w = line.strip().upper()
            if w and not w.startswith("#"):
                force_exclude.add(w)

# --- Core tier ---
core_set = set()
# Start with roots + inflections
core_set.update(core_from_roots)
# Add all structurally normal words from the full list
# (this catches most normal English words)
for w in full_list:
    if is_structurally_normal(w):
        # Keep it unless it's a known obscure 3-letter word
        if len(w) == 3 and w in OBSCURE_3 and w not in KEEP_3:
            continue
        core_set.add(w)

# Apply force include/exclude
core_set.update(w for w in force_include if w in full_set)
core_set -= force_exclude

# Remove the most extreme outliers from Core using a blocklist approach
# These are words that pass structural filters but are still obscure
CORE_BLOCKLIST_PATTERNS = re.compile(
    r"(^AALII|^AARRG|^ABMHO|^ABVOLT|^ABWATT|^ACNODE|^ADNOUN|"
    r"^AGAMIC|^AGAMID|^AGARIC|^AGAROS|^AGOUTY|^AHIMSA|^AIOLI|"
    r"^AMEBAE|^ANKUSH|^ARHAT|^ATLATL|^AZIDO|^AZOIC|"
    r"BHAJI|BURET[TE]|BYSSI|CAIRD|CALX|COYPU|CRWTH|"
    r"DHOBI|DHOTI|DJINN|DUROC|EDDO|ELHI|ENOKI|ESKAR|EYRA|"
    r"FYTTE|GADDI|GALAX|GALUT|GHAZI|GLOGG|GUANA|GYOZA|"
    r"HAPAX|HOKUM|ICTUS|IHRAM|JHEEL|JOWAR|KABAR|KANZU|KARST|"
    r"KEDGE|KEFIR|KENDO|KNISH|KOPEK|KRAAL|KUKRI|KYRIE|LAARI|"
    r"MANAT|MBIRA|MHORR|MILCH|MIRZA|MIZEN|MUDIR|NAEVI|"
    r"NIQAB|NJORD|NOYAU|NUBIA|OKAPI|ORIBI|PAEON|PAISE|"
    r"PIROG|PLAYA|PRAHU|QANAT|QINTAR|RAJAH|RAYAH|RIYAL|"
    r"SAICE|SARIN|SERAI|SHOJI|SIGIL|SOLDI|STELE|SURAH|"
    r"TALAR|TIKKA|TORII|TSUBA|UHLAN|UKASE|UMAMI|URIAL|"
    r"VEENA|WAQF|WASABI|XERIC|YENTA|ZAYIN|ZIRAM|ZLOTY|ZOEAE|ZYMUR)"
)

core_before = len(core_set)
core_set = {w for w in core_set if not CORE_BLOCKLIST_PATTERNS.search(w)}
print(f"Core blocklist removed: {core_before - len(core_set)} words")

# --- Extended tier ---
# Everything in core + structurally normal words that might be slightly obscure
extended_set = set(core_set)
for w in full_list:
    if is_structurally_normal(w):
        extended_set.add(w)

# Also add 3-letter words that aren't the MOST obscure
very_obscure_3_extended = {
    "AAL", "AAS", "ABA", "AGA", "AGS", "AHI", "AIS", "AIT", "ALA", "AMA",
    "AMU", "ANA", "ANE", "ANI", "ARD", "ARF", "ATT", "AVA", "AVO", "AWA",
    "AYS", "AZO",
    "BAP", "BAS", "BEL", "BES", "BEY", "BIS", "BOS", "BRR", "BYS",
    "CEE", "CEL", "CEP", "CIS", "COL", "COR", "COS", "COZ", "CRU", "CWM",
    "DAG", "DAH", "DAK", "DAL", "DAP", "DAW", "DEV", "DEX", "DEY",
    "DIB", "DIT", "DOL", "DOP", "DOR", "DOW", "DUP",
    "EAU", "ELD", "ELL", "ELS", "EME", "EMS", "ENG", "ENS", "ERE", "ERG",
    "ERN", "ERS", "ESS", "ETA", "ETH",
    "FAY", "FEH", "FEM", "FER", "FES", "FET", "FEU", "FEY", "FID", "FIE",
    "FIS", "FOH", "FON", "FOP", "FOU", "FOY", "FRO", "FUB", "FUD", "FUG",
    "GAE", "GAM", "GAN", "GAR", "GAT", "GED", "GEE", "GEY", "GHI",
    "GIB", "GID", "GIE", "GIP", "GIT", "GOA", "GOO", "GOR", "GOS",
    "GOX", "GUL", "GUP", "GUS", "GUV", "GYP",
    "HAE", "HAH", "HAJ", "HAO", "HAP", "HEH", "HEP", "HES", "HET", "HIC",
    "HIE", "HIN", "HMM", "HOY", "HUP",
    "ICH", "IFF", "IGG",
    "JEE", "JIN", "JOW", "JUS",
    "KAB", "KAE", "KAF", "KAS", "KAT", "KAY", "KEA", "KED", "KEF", "KEP",
    "KEX", "KHI", "KIF", "KIP", "KIR", "KIS", "KOA", "KOB", "KOP", "KOR",
    "KOS", "KUE",
    "LAC", "LAM", "LAT", "LAV", "LEK", "LEU", "LEV", "LEX", "LEY", "LIS",
    "MAE", "MAS", "MAW", "MEL", "MEM", "MHO", "MIB", "MIG", "MIL",
    "MIM", "MIR", "MIS", "MOC", "MOG", "MOL", "MOR", "MOS",
    "MUN", "MUS", "MUT", "MUX",
    "NAE", "NAM", "NAW", "NIB", "NIM", "NOB", "NOG", "NOM", "NOO", "NOS",
    "NUS",
    "OBE", "OBI", "OCA", "ODS", "OES", "OHO", "OHS", "OKA", "OKE", "OLE",
    "OMS", "ONO", "ONS", "OOH", "OOT", "OOZ", "OPE", "OPS", "ORA", "ORC",
    "ORS", "ORT", "OSE", "OUD", "OVA", "OXO", "OXY",
    "PAC", "PAH", "PAS", "PAX", "PEC", "PED", "PEH", "PES", "PHI", "PHT",
    "PIA", "PIS", "PIU", "PIX", "POH", "POI", "POL", "POM", "PSI", "PUL",
    "PUR", "PUS",
    "QAT", "QIS", "QUA",
    "RAI", "RAJ", "RAS", "RAX", "REB", "REC", "REE", "REG", "REI",
    "REM", "REP", "RES", "RET", "RHO", "RIA", "RIF", "RIN", "ROC", "ROM",
    "SAB", "SAC", "SAE", "SAU", "SEG", "SEI", "SEL", "SEN", "SER", "SHA",
    "SHH", "SIB", "SIC", "SIM", "SKA", "SOL", "SOM", "SOS", "SOT", "SOU",
    "SOX", "SUD", "SUQ", "SYN",
    "TAD", "TAE", "TAJ", "TAM", "TAO", "TAS", "TAT", "TAU", "TAV", "TAW",
    "TEG", "TET", "TEW", "THO", "TIS", "TOD", "TOG", "TOR", "TSK", "TUI",
    "TUN", "TUP", "TUT", "TWA",
    "UDO", "UDS", "UKE", "ULU", "UMM", "UNS", "UPO", "URB", "URD",
    "URP", "URS", "UTA", "UTE", "UTS",
    "VAR", "VAS", "VAU", "VAV", "VAW", "VEE", "VIG", "VIS", "VUG",
    "WAB", "WAE", "WAP", "WAT", "WAW", "WHA", "WOS", "WOT",
    "YAH", "YAR", "YEA", "YEH", "YID", "YIN", "YIP", "YOB", "YOD",
    "YOK", "YOM", "YON", "YOW", "YUK",
    "ZAX", "ZED", "ZEE", "ZEK", "ZEP", "ZIN",
}

extended_set -= very_obscure_3_extended
extended_set.update(w for w in force_include if w in full_set)
extended_set -= force_exclude

# ─── Write outputs ───────────────────────────────────────────────────────────

core_list = sorted(core_set)
extended_list = sorted(extended_set)

with open(CORE_PATH, "w") as f:
    for w in core_list:
        f.write(w + "\n")

with open(EXTENDED_PATH, "w") as f:
    for w in extended_list:
        f.write(w + "\n")

# Build removal lists
removed_from_core = sorted(full_set - core_set)
removed_from_extended = sorted(full_set - extended_set)

with open(REMOVED_CORE_PATH, "w") as f:
    f.write(f"# Words in Full but NOT in Core ({len(removed_from_core)} words)\n")
    f.write(f"# To force-include a word, add it to dict_force_include.txt\n\n")
    for w in removed_from_core:
        f.write(w + "\n")

with open(REMOVED_EXT_PATH, "w") as f:
    f.write(f"# Words in Full but NOT in Extended ({len(removed_from_extended)} words)\n")
    f.write(f"# To force-include a word, add it to dict_force_include.txt\n\n")
    for w in removed_from_extended:
        f.write(w + "\n")

# Create empty force files if they don't exist
for path in [FORCE_INCLUDE_PATH, FORCE_EXCLUDE_PATH]:
    if not os.path.exists(path):
        with open(path, "w") as f:
            f.write(f"# Add one word per line (UPPERCASE)\n")
            f.write(f"# Lines starting with # are comments\n")

# ─── Report ──────────────────────────────────────────────────────────────────

print(f"\n{'='*60}")
print(f"DICTIONARY CURATION RESULTS")
print(f"{'='*60}")
print(f"Full dictionary:     {len(full_list):>6} words")
print(f"Extended dictionary: {len(extended_list):>6} words ({len(full_list) - len(extended_list)} removed)")
print(f"Core dictionary:     {len(core_list):>6} words ({len(full_list) - len(core_list)} removed)")
print()

# Length distribution
for name, words in [("Full", full_list), ("Extended", extended_list), ("Core", core_list)]:
    by_len = {}
    for w in words:
        by_len[len(w)] = by_len.get(len(w), 0) + 1
    dist = "  ".join(f"{l}L={by_len.get(l,0):>5}" for l in range(3, 8))
    print(f"  {name:10}: {dist}")

print()
print(f"3-letter words: Full={sum(1 for w in full_list if len(w)==3)}, "
      f"Core={sum(1 for w in core_list if len(w)==3)}, "
      f"Extended={sum(1 for w in extended_list if len(w)==3)}")

# Spot check
import random
print(f"\n{'='*60}")
print("SAMPLE: 3-letter words REMOVED from Core (first 40)")
print(f"{'='*60}")
removed_3 = [w for w in removed_from_core if len(w) == 3]
for w in removed_3[:40]:
    print(f"  {w}")

print(f"\n{'='*60}")
print("SAMPLE: 3-letter words KEPT in Core (all)")
print(f"{'='*60}")
kept_3 = sorted(w for w in core_list if len(w) == 3)
# Print in rows of 15
for i in range(0, len(kept_3), 15):
    print("  " + " ".join(kept_3[i:i+15]))

print(f"\n{'='*60}")
print("SAMPLE: Random Core words (30)")
print(f"{'='*60}")
for w in sorted(random.sample(core_list, 30)):
    print(f"  {w}")

print(f"\n{'='*60}")
print("SAMPLE: Words removed from Core but kept in Extended (30)")
print(f"{'='*60}")
ext_only = sorted(extended_set - core_set)
for w in sorted(random.sample(ext_only, min(30, len(ext_only)))):
    print(f"  {w}")
