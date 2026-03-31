#!/usr/bin/env python3
"""
Dictionary Curation Filter for WordDrop
========================================
Reads dict_full.txt (51,852 enable1 words, 3-7 letters)
Produces three tiers:
  - dict_core.txt     : tight, fair, recognizable gameplay words
  - dict_extended.txt : broader but still reasonable
  - dict_full.txt     : original unchanged (reference)

Strategy:
  1. Start with a high-frequency English word list as an anchor
  2. Apply heuristic filters to catch obscure/archaic/technical words
  3. Core = passes ALL filters + frequency check
  4. Extended = passes most filters (looser thresholds)
  5. Full = everything

Run: python3 filter_dictionary.py
"""

import re
import os

SCRIPT_DIR = os.path.dirname(os.path.abspath(__file__))
FULL_PATH = os.path.join(SCRIPT_DIR, "dict_full.txt")
CORE_PATH = os.path.join(SCRIPT_DIR, "dict_core.txt")
EXTENDED_PATH = os.path.join(SCRIPT_DIR, "dict_extended.txt")
REMOVED_FROM_CORE_PATH = os.path.join(SCRIPT_DIR, "dict_removed_from_core.txt")
REMOVED_FROM_EXTENDED_PATH = os.path.join(SCRIPT_DIR, "dict_removed_from_extended.txt")

# ─── Load full word list ─────────────────────────────────────────────────────

with open(FULL_PATH) as f:
    all_words = sorted(set(line.strip() for line in f if line.strip()))

print(f"Loaded {len(all_words)} words from dict_full.txt")

# ─── HEURISTIC FILTERS ──────────────────────────────────────────────────────
#
# Each filter returns True if the word should be REMOVED (is suspicious).
# We tag words with which filters they fail, then decide tiers.

# 1. Obscure endings — archaic/technical/rare morphological patterns
OBSCURE_SUFFIXES = [
    "ACEOUS", "IFORM", "ULOUS", "ESCENT", "ITIOUS", "ATIVE",
    "ORIAL", "ASTIC", "ICAL",  # only cut when combined with rare roots
]

# Endings that are almost always obscure in 3-7 letter words
STRONG_OBSCURE_ENDINGS = re.compile(
    r"(AAHS|AALII|AALIIS|AARRGHH?)$|"
    r"^(AA[A-Z]?|ZZ)$"  # double-letter weirdness
)

# 2. Archaic/dialect patterns
ARCHAIC_PATTERNS = re.compile(
    r"(^YE[A-Z]{1,3}$)|"       # ye-words (archaic)
    r"(ETH$)|"                   # -eth endings (archaic verb forms) — but keep TEETH, SHEATH etc
    r"(EST$)"                    # -est superlatives of obscure words (handled by root check)
)

# 3. Scientific/technical prefixes that produce obscure words
TECHNICAL_PREFIXES = re.compile(
    r"^(ACRO|AERO|ALLO|ANISO|AZIDO|AZO|BARO|CARBO|CYANO|CYCLO|DEOXY|"
    r"FERRO|GALVANO|GLYCO|HALO|HETERO|HYPER|IMMUNO|KETO|LIGNO|LITHO|"
    r"MACRO|MAGNETO|MONO|MORPHO|MYCO|NANO|NEPHRO|NEURO|NITRO|OLIGO|"
    r"ORGANO|ORTHO|OSMO|OSTEO|OXO|PARA|PATHO|PERI|PHAGO|PHYLO|"
    r"PIEZO|PNEUMO|POLY|PROTO|PSEUDO|PYRO|RETRO|RHIZO|SACRO|SCHIZO|"
    r"SEISMO|SERVO|SERO|SPIRO|STEREO|SULFO|SUPRA|TECHNO|TELE|THERMO|"
    r"TROPO|TURBO|ULTRA|VASO|XENO|XYLO|ZYMO)"
)

# 4. Interjection/exclamation patterns (weird short words)
INTERJECTION_PATTERNS = re.compile(
    r"^(UGH|BAH|GAH|MEH|NAH|PAH|RAH|YAH|OOH|AAH|EEK|ACK|ARG|ERR|"
    r"HMM|SHH|TSK|TUT|OOF|OOZ|GAK|ZAP|POW|BAM|BOP|POP|HUP|OOT|"
    r"OON|AHS|OHS|EHS|UHS|OOS|OES|UGS|AGS|AIS|OIS|UIS)$"
)

# 5. Obscure 3-letter words — the biggest pain point
# These are Scrabble-only words that no normal player would recognize
OBSCURE_3_LETTER = {
    "AAH", "AAL", "AAS", "ABA", "ABO", "ABS", "ABY", "ACE", "ADO", "ADS",
    "ADZ", "AFF", "AFT", "AGA", "AGE", "AGO", "AGS", "AHA", "AHI", "AHS",
    "AID", "AIM", "AIN", "AIR", "AIS", "AIT", "ALA", "ALB", "ALE", "ALL",
    "ALP", "ALS", "ALT", "AMA", "AMI", "AMP", "AMU", "ANA", "AND", "ANE",
    "ANI", "ANT", "ANY", "APE", "APO", "APP", "APT", "ARB", "ARC", "ARD",
    "ARE", "ARF", "ARK", "ARM", "ARS", "ART", "ASH", "ASK", "ASP", "ASS",
    "ATE", "ATT", "AUK", "AVA", "AVE", "AVO", "AWA", "AWE", "AWL", "AWN",
    "AXE", "AYE", "AYS", "AZO",
    "BAD", "BAG", "BAH", "BAM", "BAN", "BAP", "BAR", "BAS", "BAT", "BAY",
    "BED", "BEE", "BEG", "BEL", "BEN", "BES", "BET", "BEY", "BIB", "BID",
    "BIG", "BIN", "BIS", "BIT", "BIZ", "BOA", "BOB", "BOD", "BOG", "BOP",
    "BOS", "BOT", "BOW", "BOX", "BOY", "BRA", "BRO", "BRR", "BUB", "BUD",
    "BUG", "BUM", "BUN", "BUP", "BUR", "BUS", "BUT", "BUY", "BYE", "BYS",
    "CAB", "CAD", "CAM", "CAN", "CAP", "CAR", "CAT", "CAW", "CEE", "CEL",
    "CEP", "CHI", "CIG", "CIS", "COB", "COD", "COG", "COL", "CON", "COO",
    "COP", "COR", "COS", "COT", "COW", "COX", "COY", "COZ", "CRU", "CRY",
    "CUB", "CUD", "CUE", "CUP", "CUR", "CUT", "CWM", "DAB", "DAD", "DAG",
    "DAH", "DAK", "DAL", "DAM", "DAP", "DAW", "DAY", "DEB", "DEE", "DEL",
    "DEN", "DEV", "DEW", "DEX", "DEY", "DIB", "DID", "DIE", "DIG", "DIM",
    "DIN", "DIP", "DIS", "DIT", "DOC", "DOE", "DOG", "DOL", "DON", "DOP",
    "DOR", "DOS", "DOT", "DOW", "DRY", "DUB", "DUD", "DUE", "DUG", "DUH",
    "DUI", "DUN", "DUO", "DUP", "DYE",
    "EAR", "EAT", "EAU", "EEL", "EEK", "EGG", "EGO", "EKE", "ELD", "ELF",
    "ELK", "ELL", "ELM", "ELS", "EME", "EMS", "EMU", "END", "ENG", "ENS",
    "EON", "ERA", "ERE", "ERG", "ERN", "ERR", "ERS", "ESS", "ETA", "ETH",
    "EVE", "EWE", "EYE",
    "FAB", "FAD", "FAG", "FAN", "FAR", "FAT", "FAX", "FAY", "FED", "FEE",
    "FEH", "FEM", "FEN", "FER", "FES", "FET", "FEU", "FEW", "FEY", "FEZ",
    "FIB", "FID", "FIE", "FIG", "FIN", "FIR", "FIS", "FIT", "FIX", "FIZ",
    "FLU", "FLY", "FOB", "FOE", "FOG", "FOH", "FON", "FOP", "FOR", "FOU",
    "FOX", "FOY", "FRO", "FRY", "FUB", "FUD", "FUG", "FUN", "FUR",
    "GAB", "GAD", "GAE", "GAG", "GAL", "GAM", "GAN", "GAP", "GAR", "GAS",
    "GAT", "GAY", "GED", "GEE", "GEL", "GEM", "GET", "GEY", "GHI", "GIB",
    "GID", "GIE", "GIG", "GIN", "GIP", "GIT", "GNU", "GOA", "GOB", "GOD",
    "GOO", "GOR", "GOS", "GOT", "GOX", "GUL", "GUM", "GUN", "GUP", "GUS",
    "GUT", "GUV", "GUY", "GYM", "GYP",
    "HAD", "HAE", "HAG", "HAH", "HAJ", "HAM", "HAO", "HAP", "HAS", "HAT",
    "HAW", "HAY", "HEH", "HEN", "HEP", "HER", "HES", "HET", "HEW", "HEX",
    "HEY", "HIC", "HID", "HIE", "HIM", "HIN", "HIP", "HIS", "HIT", "HMM",
    "HOB", "HOD", "HOE", "HOG", "HOP", "HOT", "HOW", "HOY", "HUB", "HUE",
    "HUG", "HUH", "HUM", "HUN", "HUP", "HUT",
    "ICE", "ICH", "ICK", "ICY", "IDS", "IFF", "IFS", "IGG", "ILK", "ILL",
    "IMP", "INK", "INN", "INS", "ION", "IRE", "IRK", "ISM", "ITS", "IVY",
    "JAB", "JAG", "JAM", "JAR", "JAW", "JAY", "JEE", "JET", "JIB", "JIG",
    "JIN", "JOB", "JOE", "JOG", "JOT", "JOW", "JOY", "JUG", "JUN", "JUS",
    "JUT",
    "KAB", "KAE", "KAF", "KAS", "KAT", "KAY", "KEA", "KED", "KEF", "KEG",
    "KEN", "KEP", "KEX", "KEY", "KHI", "KID", "KIF", "KIN", "KIP", "KIR",
    "KIS", "KIT", "KOA", "KOB", "KOI", "KOP", "KOR", "KOS", "KUE",
    "LAB", "LAC", "LAD", "LAG", "LAM", "LAP", "LAS", "LAT", "LAV", "LAW",
    "LAX", "LAY", "LEA", "LED", "LEE", "LEG", "LEI", "LEK", "LES", "LET",
    "LEU", "LEV", "LEX", "LEY", "LIB", "LID", "LIE", "LIN", "LIP", "LIS",
    "LIT", "LOG", "LOO", "LOP", "LOT", "LOW", "LUG", "LUV", "LUX", "LYE",
    "MAC", "MAD", "MAE", "MAG", "MAN", "MAP", "MAR", "MAS", "MAT", "MAW",
    "MAX", "MAY", "MED", "MEL", "MEM", "MEN", "MET", "MEW", "MHO", "MIB",
    "MIC", "MID", "MIG", "MIL", "MIM", "MIR", "MIS", "MIX", "MOB", "MOC",
    "MOD", "MOG", "MOL", "MOM", "MON", "MOO", "MOP", "MOR", "MOS", "MOT",
    "MOW", "MUD", "MUG", "MUM", "MUN", "MUS", "MUT", "MUX",
    "NAB", "NAE", "NAG", "NAH", "NAM", "NAN", "NAP", "NAW", "NAY", "NEE",
    "NET", "NEW", "NIB", "NIL", "NIM", "NIP", "NIT", "NIX", "NOB", "NOD",
    "NOG", "NOM", "NOO", "NOR", "NOS", "NOT", "NOW", "NUB", "NUN", "NUS",
    "NUT",
    "OAF", "OAK", "OAR", "OAT", "OBE", "OBI", "OCA", "ODD", "ODE", "ODS",
    "OES", "OFF", "OFT", "OHM", "OHO", "OHS", "OIL", "OKA", "OKE", "OLD",
    "OLE", "OMS", "ONE", "ONO", "ONS", "OOH", "OOT", "OOZ", "OPE", "OPS",
    "OPT", "ORA", "ORB", "ORC", "ORE", "ORS", "ORT", "OSE", "OUD", "OUR",
    "OUT", "OVA", "OWE", "OWL", "OWN", "OXO", "OXY",
    "PAC", "PAD", "PAH", "PAL", "PAM", "PAN", "PAP", "PAR", "PAS", "PAT",
    "PAW", "PAX", "PAY", "PEA", "PEC", "PED", "PEE", "PEG", "PEH", "PEN",
    "PEP", "PER", "PES", "PET", "PEW", "PHI", "PHT", "PIA", "PIC", "PIE",
    "PIG", "PIN", "PIP", "PIS", "PIT", "PIU", "PIX", "PLY", "POD", "POH",
    "POI", "POL", "POM", "POP", "POT", "POW", "POX", "PRO", "PRY", "PSI",
    "PUB", "PUD", "PUG", "PUL", "PUN", "PUP", "PUR", "PUS", "PUT",
    "QAT", "QIS", "QUA",
    "RAD", "RAG", "RAH", "RAI", "RAJ", "RAM", "RAN", "RAP", "RAS", "RAT",
    "RAW", "RAX", "RAY", "REB", "REC", "RED", "REE", "REF", "REG", "REI",
    "REM", "REP", "RES", "RET", "REV", "REX", "RHO", "RIA", "RIB", "RID",
    "RIF", "RIG", "RIM", "RIN", "RIP", "ROB", "ROC", "ROD", "ROE", "ROM",
    "ROT", "ROW", "RUB", "RUE", "RUG", "RUM", "RUN", "RUT", "RYA", "RYE",
    "SAB", "SAC", "SAD", "SAE", "SAG", "SAL", "SAP", "SAT", "SAU", "SAW",
    "SAX", "SAY", "SEA", "SEC", "SEE", "SEG", "SEI", "SEL", "SEN", "SER",
    "SET", "SEW", "SHA", "SHE", "SHH", "SHY", "SIB", "SIC", "SIM", "SIN",
    "SIP", "SIR", "SIS", "SIT", "SIX", "SKA", "SKI", "SKY", "SLY", "SOB",
    "SOD", "SOL", "SOM", "SON", "SOP", "SOS", "SOT", "SOU", "SOW", "SOX",
    "SOY", "SPA", "SPY", "STY", "SUB", "SUD", "SUE", "SUM", "SUN", "SUP",
    "SUQ", "SYN",
    "TAB", "TAD", "TAE", "TAG", "TAJ", "TAM", "TAN", "TAO", "TAP", "TAR",
    "TAS", "TAT", "TAU", "TAV", "TAW", "TAX", "TEA", "TED", "TEE", "TEG",
    "TEN", "TET", "TEW", "THE", "THO", "THY", "TIC", "TIE", "TIN", "TIP",
    "TIS", "TIT", "TOD", "TOE", "TOG", "TOM", "TON", "TOO", "TOP", "TOR",
    "TOT", "TOW", "TOY", "TSK", "TUB", "TUG", "TUI", "TUN", "TUP", "TUT",
    "TUX", "TWA", "TWO",
    "UDO", "UDS", "UGH", "UKE", "ULU", "UMM", "UMP", "UNI", "UNS", "UPO",
    "UPS", "URB", "URD", "URN", "URP", "URS", "USE", "UTA", "UTE", "UTS",
    "VAC", "VAN", "VAR", "VAS", "VAT", "VAU", "VAV", "VAW", "VEE", "VEG",
    "VET", "VEX", "VIA", "VID", "VIE", "VIG", "VIM", "VIS", "VOW", "VOX",
    "VUG",
    "WAB", "WAD", "WAE", "WAG", "WAN", "WAP", "WAR", "WAS", "WAT", "WAW",
    "WAX", "WAY", "WEB", "WED", "WEE", "WEN", "WET", "WHA", "WHO", "WHY",
    "WIG", "WIN", "WIS", "WIT", "WIZ", "WOE", "WOG", "WOK", "WON", "WOO",
    "WOP", "WOS", "WOT", "WOW",
    "YAH", "YAK", "YAM", "YAP", "YAR", "YAW", "YAY", "YEA", "YEH", "YEN",
    "YEP", "YES", "YET", "YEW", "YID", "YIN", "YIP", "YOB", "YOD", "YOK",
    "YOM", "YON", "YOU", "YOW", "YUK", "YUM", "YUP",
    "ZAG", "ZAP", "ZAX", "ZED", "ZEE", "ZEK", "ZEN", "ZEP", "ZIG", "ZIN",
    "ZIP", "ZIT", "ZOA", "ZOO",
}

# This is actually a KEEP list for common 3-letter words
# Let me restructure...

# Actually, let me take a different approach. Instead of trying to enumerate
# all the good words (impossible), I'll build REMOVAL heuristics.

# ─── KEEP LISTS (force-include regardless of filters) ────────────────────────

# Common words that might accidentally get filtered
FORCE_KEEP = set()

# Load a common English word frequency anchor
# We'll use simple heuristics instead of external data

# ─── REMOVAL HEURISTICS ─────────────────────────────────────────────────────

def has_no_vowel(word):
    """Words with no standard vowels (A,E,I,O,U,Y) — very obscure"""
    return not any(c in word for c in "AEIOUY")

def has_triple_consonant_cluster(word):
    """Unusual triple+ consonant clusters that signal obscure words"""
    consonants = "BCDFGHJKLMNPQRSTVWXZ"
    count = 0
    for c in word:
        if c in consonants:
            count += 1
            if count >= 4:
                return True
        else:
            count = 0
    return False

def has_double_vowel_exotic(word):
    """Exotic double vowel patterns (AA, II, UU, OO at start, etc.)"""
    if word.startswith("AA") and word not in {"AARDVARK"}:
        return True
    if "II" in word and word not in {"BIKINI", "SKIING", "RADII", "ALIBI"}:
        return True
    if "UU" in word:
        return True
    return False

# Endings that strongly signal obscure/technical words
OBSCURE_ENDING_RE = re.compile(
    r"(ACEOUS|IFORM|ULENT|ULOUS|ESCENT|ITIOUS|ORIAL|ASTIC|"
    r"AGEAL|ANDRY|IATRY|IASIS|ACITY|OSITY|ARCHY|URGY|GAMY|"
    r"PATHY|SOPHY|SCOPY|METRY|LYSIS|GENIC|GOGUE|CIDAL|CRACY|"
    r"PHYTE|TAXIS|LEXIA|NOMIA|GNOMY|GLYPH|MORPH|TROPH|PHILIC|"
    r"PHOBIC|CYTIC|BLASTIC|CLASTIC)$"
)

# Prefixes that in 3-7 letter words almost always produce obscure results
OBSCURE_PREFIX_RE = re.compile(
    r"^(AALII|ABMHO|ABVOLT|ABWATT|ACNODE|ADNOUN|AGAMA|AGARIC|AGENE|"
    r"AHIMSA|AIOLI|AMEBAE|ANKH|ARHAT|ARIEL|ATLATL|AZIDO|AZOIC|"
    r"BHAJI|BOYAR|BURET|BYSSI|CAIRD|CALX|CAPH|COYPU|CRWTH|CWMS|"
    r"DHIKR|DHOBI|DHOTI|DJINN|DUROC|DUVET|EDDO|ELHI|ENOKI|ERGOT|"
    r"ESKAR|EYRA|FROE|FYTTE|GADDI|GALAX|GALUT|GHAZI|GHEE|GLOGG|"
    r"GLYPH|GUANA|GYOZA|HAIKU|HAJJ|HAPAX|HIJAB|HIJRA|HOKUM|HUTCH|"
    r"ICTUS|IGLOO|IHRAM|JIAO|JHEEL|JOWAR|KABAR|KANZU|KAPPA|KARST|"
    r"KEDGE|KEELSON|KEFIR|KENDO|KHAKI|KNISH|KOPEK|KRAAL|KUKRI|"
    r"KYRIE|LAARI|LWEI|MANAT|MBIRA|MHORR|MILCH|MIRZA|MIZEN|MOREL|"
    r"MUDIR|NAEVI|NGOMA|NIQAB|NJORD|NOYAU|NUBIA|OKAPI|ORIBI|"
    r"PAEON|PAISE|PIETA|PILAF|PIROG|PLAYA|PRAHU|QANAT|QINTAR|QUAFF|"
    r"RAJAH|RAYAH|RIYAL|RUPEE|SAICE|SARIN|SERAI|SHOJI|SIGIL|SOLDI|"
    r"STELE|SURAH|TALAR|THANE|TIKKA|TORII|TSUBA|UHLAN|UKASE|UMAMI|"
    r"URIAL|VEENA|VOILA|WAQF|WASABI|XERIC|YENTA|ZAYIN|ZIRAM|ZLOTY|ZOEAE)$"
)

# Obscure 3-letter words to REMOVE (Scrabble-only, not recognized by normal players)
OBSCURE_3_REMOVE = {
    "AAH", "AAL", "AAS", "ABA", "ABO", "ABY", "ACE", "ADZ", "AFF", "AGA",
    "AGS", "AHI", "AHS", "AIN", "AIS", "AIT", "ALA", "ALB", "ALS", "ALT",
    "AMA", "AMI", "AMU", "ANA", "ANE", "ANI", "APO", "ARB", "ARD", "ARF",
    "ARS", "ATT", "AUK", "AVA", "AVO", "AWA", "AWN", "AYS", "AZO",
    "BAH", "BAP", "BAS", "BEL", "BEN", "BES", "BEY", "BIS", "BOA", "BOD",
    "BOS", "BOT", "BRA", "BRR", "BUB", "BUR", "BYS",
    "CAW", "CEE", "CEL", "CEP", "CHI", "CIG", "CIS", "COB", "COG", "COL",
    "COR", "COS", "COZ", "CRU", "CWM",
    "DAB", "DAG", "DAH", "DAK", "DAL", "DAP", "DAW", "DEL", "DEV", "DEX",
    "DEY", "DIB", "DIS", "DIT", "DOC", "DOL", "DOP", "DOR", "DOS", "DOW",
    "DUI", "DUN", "DUP",
    "EAU", "ELD", "ELL", "ELS", "EME", "EMS", "ENG", "ENS", "ERE", "ERG",
    "ERN", "ERR", "ERS", "ESS", "ETA", "ETH", "EWE",
    "FAY", "FEH", "FEM", "FER", "FES", "FET", "FEU", "FEY", "FID", "FIE",
    "FIR", "FIS", "FOB", "FOH", "FON", "FOP", "FOU", "FOY", "FRO", "FUB",
    "FUD", "FUG",
    "GAB", "GAE", "GAL", "GAM", "GAN", "GAR", "GAT", "GED", "GEE", "GEY",
    "GHI", "GIB", "GID", "GIE", "GIP", "GIT", "GOA", "GOB", "GOO", "GOR",
    "GOS", "GOX", "GUL", "GUP", "GUS", "GUV", "GYP",
    "HAE", "HAH", "HAJ", "HAO", "HAP", "HEH", "HEP", "HES", "HET", "HIC",
    "HIE", "HIN", "HMM", "HOB", "HOD", "HOY", "HUP",
    "ICH", "IDS", "IFF", "IGG", "ILK", "INS", "IRE",
    "JEE", "JIB", "JIN", "JOE", "JOW", "JUS",
    "KAB", "KAE", "KAF", "KAS", "KAT", "KAY", "KEA", "KED", "KEF", "KEP",
    "KEX", "KHI", "KIF", "KIP", "KIR", "KIS", "KOA", "KOB", "KOP", "KOR",
    "KOS", "KUE",
    "LAC", "LAM", "LAS", "LAT", "LAV", "LEA", "LEI", "LEK", "LES", "LEU",
    "LEV", "LEX", "LEY", "LIS",
    "MAE", "MAR", "MAS", "MAW", "MEL", "MEM", "MHO", "MIB", "MIG", "MIL",
    "MIM", "MIR", "MIS", "MOC", "MOG", "MOL", "MOR", "MOS", "MUN", "MUS",
    "MUT", "MUX",
    "NAB", "NAE", "NAH", "NAM", "NAW", "NEE", "NIB", "NIL", "NIM", "NIX",
    "NOB", "NOG", "NOM", "NOO", "NOR", "NOS", "NUB", "NUS",
    "OBE", "OBI", "OCA", "ODS", "OES", "OHO", "OHS", "OKA", "OKE", "OLE",
    "OMS", "ONO", "ONS", "OOH", "OOT", "OOZ", "OPE", "OPS", "ORA", "ORC",
    "ORS", "ORT", "OSE", "OUD", "OVA", "OXO", "OXY",
    "PAC", "PAH", "PAM", "PAS", "PAW", "PAX", "PEC", "PED", "PEH", "PEP",
    "PES", "PHI", "PHT", "PIA", "PIS", "PIU", "PIX", "POD", "POH", "POI",
    "POL", "POM", "POW", "PSI", "PUL", "PUR", "PUS",
    "QAT", "QIS", "QUA",
    "RAH", "RAI", "RAJ", "RAS", "RAX", "REB", "REC", "REE", "REG", "REI",
    "REM", "REP", "RES", "RET", "REV", "RHO", "RIA", "RIF", "RIN", "ROC",
    "ROM",
    "SAB", "SAC", "SAE", "SAU", "SEG", "SEI", "SEL", "SEN", "SER", "SHA",
    "SHH", "SIB", "SIC", "SIM", "SKA", "SOD", "SOL", "SOM", "SOP", "SOS",
    "SOT", "SOU", "SOX", "STY", "SUD", "SUQ", "SYN",
    "TAD", "TAE", "TAJ", "TAM", "TAO", "TAS", "TAT", "TAU", "TAV", "TAW",
    "TEG", "TET", "TEW", "THO", "TIS", "TOD", "TOG", "TOR", "TSK", "TUI",
    "TUN", "TUP", "TUT", "TWA",
    "UDO", "UDS", "UGH", "UKE", "ULU", "UMM", "UNS", "UPO", "UPS", "URB",
    "URD", "URP", "URS", "UTA", "UTE", "UTS",
    "VAC", "VAR", "VAS", "VAU", "VAV", "VAW", "VEE", "VID", "VIE", "VIG",
    "VIS", "VUG",
    "WAB", "WAD", "WAE", "WAP", "WAT", "WAW", "WEN", "WHA", "WIS", "WOE",
    "WOG", "WON", "WOP", "WOS", "WOT",
    "YAH", "YAR", "YAW", "YEA", "YEH", "YEW", "YID", "YIN", "YIP", "YOB",
    "YOD", "YOK", "YOM", "YON", "YOW", "YUK",
    "ZAG", "ZAX", "ZED", "ZEE", "ZEK", "ZEN", "ZEP", "ZIN",
}

# Common 3-letter words that should ALWAYS stay (override any filter)
COMMON_3_KEEP = {
    "ACE", "ACT", "ADD", "AGE", "AGO", "AID", "AIM", "AIR", "ALL", "AND",
    "ANT", "ANY", "APE", "ARC", "ARE", "ARK", "ARM", "ART", "ASH", "ASK",
    "ATE", "AWE", "AXE", "AYE",
    "BAD", "BAG", "BAM", "BAN", "BAR", "BAT", "BAY", "BED", "BEE", "BEG",
    "BET", "BIG", "BIN", "BIT", "BOB", "BOG", "BOP", "BOW", "BOX", "BOY",
    "BRO", "BUD", "BUG", "BUM", "BUN", "BUS", "BUT", "BUY", "BYE",
    "CAB", "CAD", "CAM", "CAN", "CAP", "CAR", "CAT", "COD", "CON", "COO",
    "COP", "COT", "COW", "COX", "COY", "CRY", "CUB", "CUD", "CUE", "CUP",
    "CUR", "CUT",
    "DAB", "DAD", "DAM", "DAY", "DEB", "DEN", "DEW", "DID", "DIE", "DIG",
    "DIM", "DIN", "DIP", "DOC", "DOE", "DOG", "DON", "DOT", "DRY", "DUB",
    "DUD", "DUE", "DUG", "DUH", "DUN", "DUO", "DYE",
    "EAR", "EAT", "EEL", "EGG", "EGO", "EKE", "ELF", "ELK", "ELM", "EMU",
    "END", "EON", "ERA", "EVE", "EWE", "EYE",
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
    "JOT", "JOY", "JUG", "JUT",
    "KEG", "KEN", "KEY", "KID", "KIN", "KIT",
    "LAB", "LAD", "LAG", "LAP", "LAW", "LAX", "LAY", "LED", "LEE", "LEG",
    "LET", "LID", "LIE", "LIN", "LIP", "LIT", "LOG", "LOO", "LOP", "LOT",
    "LOW", "LUG",
    "MAD", "MAN", "MAP", "MAT", "MAX", "MAY", "MED", "MEN", "MET", "MEW",
    "MIC", "MID", "MIX", "MOB", "MOD", "MOM", "MON", "MOO", "MOP", "MOT",
    "MOW", "MUD", "MUG", "MUM",
    "NAB", "NAG", "NAP", "NAY", "NET", "NEW", "NIL", "NIP", "NIT", "NOD",
    "NOR", "NOT", "NOW", "NUB", "NUN", "NUT",
    "OAF", "OAK", "OAR", "OAT", "ODD", "ODE", "OFF", "OFT", "OHM", "OIL",
    "OLD", "ONE", "OPT", "ORB", "ORE", "OUR", "OUT", "OWE", "OWL", "OWN",
    "PAD", "PAL", "PAN", "PAP", "PAR", "PAT", "PAY", "PEA", "PEE", "PEG",
    "PEN", "PEP", "PER", "PET", "PEW", "PIE", "PIG", "PIN", "PIP", "PIT",
    "PLY", "POD", "POP", "POT", "POW", "POX", "PRO", "PRY", "PUB", "PUD",
    "PUG", "PUN", "PUP", "PUT",
    "RAD", "RAG", "RAM", "RAN", "RAP", "RAT", "RAW", "RAY", "RED", "REF",
    "RIB", "RID", "RIG", "RIM", "RIP", "ROB", "ROD", "ROE", "ROT", "ROW",
    "RUB", "RUE", "RUG", "RUM", "RUN", "RUT", "RYE",
    "SAD", "SAG", "SAP", "SAT", "SAW", "SAX", "SAY", "SEA", "SEC", "SEE",
    "SET", "SEW", "SHE", "SHY", "SIN", "SIP", "SIR", "SIS", "SIT", "SIX",
    "SKI", "SKY", "SLY", "SOB", "SOD", "SON", "SOP", "SOW", "SOY", "SPA",
    "SPY", "STY", "SUB", "SUE", "SUM", "SUN", "SUP",
    "TAB", "TAG", "TAN", "TAP", "TAR", "TAX", "TEA", "TEN", "THE", "THY",
    "TIC", "TIE", "TIN", "TIP", "TIT", "TOE", "TOM", "TON", "TOO", "TOP",
    "TOT", "TOW", "TOY", "TUB", "TUG", "TUX", "TWO",
    "URN", "USE",
    "VAN", "VAT", "VEG", "VET", "VEX", "VIA", "VIM", "VOW",
    "WAD", "WAG", "WAR", "WAS", "WAX", "WAY", "WEB", "WED", "WEE", "WET",
    "WHO", "WHY", "WIG", "WIN", "WIT", "WIZ", "WOE", "WOK", "WON", "WOO",
    "WOW",
    "YAK", "YAM", "YAP", "YAW", "YAY", "YEN", "YEP", "YES", "YET", "YEW",
    "YOU", "YUM", "YUP",
    "ZAG", "ZAP", "ZEN", "ZIP", "ZIT", "ZOO",
}

# ─── Obscure word patterns (for 4-7 letter words) ───────────────────────────

# Words ending in these are almost always obscure in a casual game context
OBSCURE_ENDINGS_STRONG = re.compile(
    r"(AAHS|AALS|ABMHO|AALII|AHED|"
    r"OSES|ASES|ESES|ISES|USES|"  # obscure plural patterns
    r"OIDS|ULES|INES|ENES|ANES|ONES|ITES|ATES|"  # only when root is obscure
    r"AGMS|ALMS|YLLS|PSIS|"
    r"OIDAL|OIDEA|IFORM|ULOUS)$"
)

# ─── Frequency-based filtering using word structure ──────────────────────────

def looks_obscure(word):
    """
    Heuristic: does this word LOOK obscure to a normal English speaker?
    Returns (is_obscure, reason) tuple.
    """
    w = word.upper()
    wlen = len(w)

    # Force-keep common words
    if w in COMMON_3_KEEP:
        return False, "common"

    # --- 3-letter word filter ---
    if wlen == 3 and w in OBSCURE_3_REMOVE:
        return True, "obscure_3letter"

    # No vowels
    if has_no_vowel(w):
        return True, "no_vowel"

    # Double exotic vowels
    if has_double_vowel_exotic(w):
        return True, "exotic_double_vowel"

    # Q without QU (almost always obscure)
    if "Q" in w and "QU" not in w:
        return True, "q_without_qu"

    # Words starting with uncommon letter combos
    uncommon_starts = [
        "BH", "BW", "CW", "DH", "DZ", "FJ", "GH", "GN", "HM",
        "KH", "KN", "LH", "MH", "NH", "NJ", "PF", "PN", "PS",
        "QA", "QI", "RH", "SR", "SV", "TH", "TS", "UH",
        "VL", "WR", "XE", "YL", "ZH", "ZW", "ZY",
    ]
    # Keep common ones: TH, WR, KN, GN, PS are actually common
    common_uncommon_starts = {"TH", "WR", "KN", "GN", "PS", "PH", "SH", "CH", "WH", "QU", "SC", "SK", "SL", "SM", "SN", "SP", "ST", "SW"}

    if wlen >= 3:
        start2 = w[:2]
        if start2 in uncommon_starts and start2 not in common_uncommon_starts:
            return True, f"uncommon_start_{start2}"

    return False, "ok"


def is_likely_common(word):
    """
    Heuristic for whether a word is likely in common everyday usage.
    Uses structural patterns rather than frequency data.
    """
    w = word.upper()
    wlen = len(w)

    # Very common short words are almost always fine
    if w in COMMON_3_KEEP:
        return True

    # Common English word patterns
    # Words ending in common suffixes from common roots
    common_endings = [
        "ING", "TION", "SION", "NESS", "MENT", "ABLE", "IBLE",
        "TING", "TED", "LY", "ER", "ED", "ES", "AL", "FUL",
        "LESS", "ISH", "OUS", "IVE", "ENT", "ANT", "ARY",
        "IST", "ISM", "IZE", "ATE", "URE", "AGE", "ICE",
        "IGHT", "OUND", "IGHT", "AND", "OOK", "EAR", "EAT",
        "OWN", "AID", "AIR", "EAD", "EAM", "EAN", "EAP",
    ]

    # Check if word has common morphological structure
    for ending in common_endings:
        if w.endswith(ending) and wlen > len(ending) + 1:
            return True

    # Common 4-letter word patterns
    if wlen == 4:
        # CVC + common ending, CVCC, CCVC patterns — too broad to enumerate
        pass

    return False  # uncertain — don't force include


# ─── EXTENDED tier: looser filter ────────────────────────────────────────────

def looks_very_obscure(word):
    """
    Stricter filter — only removes the MOST obscure words.
    Extended tier keeps everything except these.
    """
    w = word.upper()
    wlen = len(w)

    if w in COMMON_3_KEEP:
        return False, "common"

    # No vowels at all
    if has_no_vowel(w):
        return True, "no_vowel"

    # Q without QU
    if "Q" in w and "QU" not in w:
        return True, "q_without_qu"

    # Triple/quad consonant clusters
    if has_triple_consonant_cluster(w):
        # But allow common ones: IGHTS, ENGTH, etc
        common_clusters = {"NGTH", "GHTS", "NTHS", "RLDS", "NKTH"}
        has_common = any(cc in w for cc in common_clusters)
        if not has_common:
            return True, "consonant_cluster"

    # Very exotic starts
    very_exotic_starts = ["BH", "BW", "CW", "DZ", "FJ", "GH", "HM",
                          "MH", "NH", "NJ", "PF", "SR", "SV", "VL",
                          "ZH", "ZW", "ZY"]
    if wlen >= 3 and w[:2] in very_exotic_starts:
        return True, f"very_exotic_start"

    # AA/II/UU patterns
    if w.startswith("AA") or "UU" in w:
        return True, "exotic_vowel"

    # The most obscure 3-letter words
    very_obscure_3 = {
        "AAL", "AAS", "ABA", "ABO", "ABY", "AGA", "AGS", "AHI", "AHS",
        "AIS", "AIT", "ALA", "ALB", "AMA", "AMU", "ANA", "ANE", "ANI",
        "ARD", "ARF", "ATT", "AVA", "AVO", "AWA", "AYS", "AZO",
        "BAP", "BEL", "BES", "BEY", "BIS", "BOS", "BRR", "BYS",
        "CEE", "CEL", "CEP", "CHI", "CIS", "COG", "COL", "COR", "COS",
        "COZ", "CRU", "CWM",
        "DAG", "DAH", "DAK", "DAL", "DAP", "DAW", "DEL", "DEV", "DEX",
        "DEY", "DIB", "DIT", "DOL", "DOP", "DOR", "DOW", "DUP",
        "EAU", "ELD", "ELL", "ELS", "EME", "EMS", "ENG", "ENS", "ERE",
        "ERG", "ERN", "ERS", "ESS", "ETA", "ETH",
        "FAY", "FEH", "FEM", "FER", "FES", "FET", "FEU", "FEY", "FID",
        "FIE", "FIR", "FIS", "FOH", "FON", "FOP", "FOU", "FOY", "FRO",
        "FUB", "FUD", "FUG",
        "GAB", "GAE", "GAM", "GAN", "GAR", "GAT", "GED", "GEE", "GEY",
        "GHI", "GIB", "GID", "GIE", "GIP", "GIT", "GOA", "GOO", "GOR",
        "GOS", "GOX", "GUL", "GUP", "GUS", "GUV", "GYP",
        "HAE", "HAH", "HAJ", "HAO", "HAP", "HEH", "HEP", "HES", "HET",
        "HIC", "HIE", "HIN", "HUP",
        "ICH", "IFF", "IGG",
        "JEE", "JIN", "JOW", "JUS",
        "KAB", "KAE", "KAF", "KAS", "KAT", "KAY", "KEA", "KED", "KEF",
        "KEP", "KEX", "KHI", "KIF", "KIP", "KIR", "KIS", "KOA", "KOB",
        "KOP", "KOR", "KOS", "KUE",
        "LAC", "LAM", "LAT", "LAV", "LEK", "LEU", "LEV", "LEX", "LEY",
        "LIS",
        "MAE", "MAS", "MAW", "MEL", "MEM", "MHO", "MIB", "MIG", "MIL",
        "MIM", "MIR", "MIS", "MOC", "MOG", "MOL", "MOR", "MOS", "MUN",
        "MUS", "MUT", "MUX",
        "NAE", "NAM", "NAW", "NIB", "NIM", "NIX", "NOB", "NOG", "NOM",
        "NOO", "NOS", "NUS",
        "OBE", "OBI", "OCA", "ODS", "OES", "OHO", "OHS", "OKA", "OKE",
        "OLE", "OMS", "ONO", "ONS", "OOH", "OOT", "OOZ", "OPE", "OPS",
        "ORA", "ORS", "ORT", "OSE", "OUD", "OVA", "OXO", "OXY",
        "PAC", "PAH", "PAM", "PAS", "PAX", "PEC", "PED", "PEH",
        "PES", "PHI", "PHT", "PIA", "PIS", "PIU", "PIX", "POH", "POI",
        "POL", "POM", "PSI", "PUL", "PUR", "PUS",
        "QAT", "QIS", "QUA",
        "RAI", "RAJ", "RAS", "RAX", "REB", "REC", "REE", "REG", "REI",
        "REM", "REP", "RES", "RET", "RHO", "RIA", "RIF", "RIN", "ROC",
        "ROM",
        "SAB", "SAC", "SAE", "SAU", "SEG", "SEI", "SEL", "SEN", "SER",
        "SHA", "SIB", "SIC", "SIM", "SKA", "SOL", "SOM", "SOS", "SOT",
        "SOU", "SOX", "SUD", "SUQ", "SYN",
        "TAE", "TAJ", "TAM", "TAO", "TAS", "TAT", "TAU", "TAV", "TAW",
        "TEG", "TET", "TEW", "THO", "TIS", "TOD", "TOG", "TOR", "TSK",
        "TUI", "TUN", "TUP", "TUT", "TWA",
        "UDO", "UDS", "UKE", "ULU", "UMM", "UNS", "UPO", "URB", "URD",
        "URP", "URS", "UTA", "UTE", "UTS",
        "VAR", "VAS", "VAU", "VAV", "VAW", "VEE", "VIG", "VIS", "VUG",
        "WAB", "WAE", "WAP", "WAT", "WAW", "WHA", "WOS", "WOT",
        "YAR", "YEA", "YEH", "YOB", "YOD", "YOK", "YOM", "YON", "YOW",
        "YUK",
        "ZAX", "ZED", "ZEE", "ZEK", "ZEP", "ZIN",
    }
    if wlen == 3 and w in very_obscure_3:
        return True, "very_obscure_3letter"

    return False, "ok"


# ─── Build additional obscure word patterns for 4-7 letter words ─────────────

# Words with these substrings are almost always obscure
OBSCURE_SUBSTRINGS = [
    "AALII", "AARGH", "AARRG", "ABMHO", "ABVOLT", "ABWATT",
    "PHYTE", "GNATH", "GLYPH", "MORPH",
]

# Endings strongly associated with obscure/technical terms
TECHNICAL_ENDINGS = re.compile(
    r"(EMIA|URIA|OSIS|ITIS|ECTOMY|OTOMY|OLOGY|SOPHY|PATHY|"
    r"METRY|SCOPY|CRACY|ARCHY|GAMY|ANDRY|CIDE|PHAGE|PHORE|"
    r"BLAST|CLAST|PLASM|TROPH|CHROME|STATIC|GENIC|LYTIC|"
    r"XYLIC|PYRIC|CYLIC|AZOLE|AZINE|OXIME|AMIDE|AZIDE|"
    r"THANE|PHANE|BORON|ERGIC|AMINE)$"
)

# Endings common in obscure Scrabble words but rare in casual English
SCRABBLE_ENDINGS = re.compile(
    r"(AAHED|AAHING|OIDAL|OIDEA|IFORM|ULOUS|ACEAN|AGEAL|"
    r"ATILE|ATIVE|ORIAL|ULENT|INENT|OSCOPY|ASTIC)$"
)


def score_obscurity(word):
    """
    Returns an obscurity score 0-100. Higher = more obscure.
    0-30: likely common
    31-60: questionable
    61-100: likely obscure
    """
    w = word.upper()
    wlen = len(w)
    score = 0

    # --- Positive signals (reduce score = more common) ---
    if w in COMMON_3_KEEP:
        return 0

    # Common everyday patterns
    common_4 = {"ABLE", "BACK", "BALL", "BAND", "BANK", "BASE", "BATH",
                "BEAR", "BEAT", "BEEN", "BEER", "BELL", "BELT", "BEND",
                "BEST", "BIKE", "BILL", "BIND", "BIRD", "BITE", "BLOW",
                "BLUE", "BOAT", "BODY", "BOLD", "BOLT", "BOMB", "BOND",
                "BONE", "BOOK", "BOOT", "BORE", "BORN", "BOSS", "BOTH",
                "BOWL", "BRED", "BREW", "BULK", "BULL", "BURN", "BUSY",
                "CAFE", "CAGE", "CAKE", "CALL", "CALM", "CAME", "CAMP",
                "CARD", "CARE", "CART", "CASE", "CASH", "CAST", "CAVE",
                "CELL", "CHAT", "CHIP", "CHOP", "CITY", "CLAM", "CLAP",
                "CLAY", "CLIP", "CLUB", "CLUE", "COAL", "COAT", "CODE",
                "COIN", "COLD", "COLE", "COME", "COOK", "COOL", "COPE",
                "COPY", "CORD", "CORE", "CORK", "CORN", "COST", "COZY",
                "CRAB", "CREW", "CROP", "CROW", "CUBE", "CURB", "CURE",
                "CURL", "CUTE", "DALE", "DAME", "DAMP", "DARE", "DARK",
                "DART", "DASH", "DATA", "DATE", "DAWN", "DEAD", "DEAF",
                "DEAL", "DEAR", "DECK", "DEED", "DEEM", "DEEP", "DEER",
                "DEMO", "DENT", "DENY", "DESK", "DIAL", "DICE", "DIET",
                "DIME", "DINE", "DIRE", "DIRT", "DISH", "DISK", "DOCK",
                "DOES", "DOME", "DONE", "DOOM", "DOOR", "DOSE", "DOWN",
                "DRAG", "DRAW", "DREW", "DRIP", "DROP", "DRUM", "DUAL",
                "DUCK", "DUDE", "DUEL", "DUKE", "DULL", "DUMB", "DUMP",
                "DUNE", "DUNK", "DUSK", "DUST", "DUTY", "EACH", "EARN",
                "EASE", "EAST", "EASY", "EDGE", "EDIT", "ELSE", "EMIT",
                "EPIC", "EURO", "EVEN", "EVER", "EVIL", "EXAM", "EXEC",
                "EXIT", "FACE", "FACT", "FADE", "FAIL", "FAIR", "FAKE",
                "FALL", "FAME", "FANG", "FARE", "FARM", "FAST", "FATE",
                "FAWN", "FEAR", "FEAT", "FEED", "FEEL", "FEET", "FELL",
                "FELT", "FERN", "FEST", "FILE", "FILL", "FILM", "FIND",
                "FINE", "FIRE", "FIRM", "FISH", "FIST", "FLAG", "FLAME",
                "FLAP", "FLAT", "FLAW", "FLED", "FLEW", "FLEX", "FLIP",
                "FLOCK", "FLOG", "FLOW", "FOAM", "FOCUS", "FOLD", "FOLK",
                "FOND", "FONT", "FOOD", "FOOL", "FOOT", "FORD", "FORE",
                "FORK", "FORM", "FORT", "FOUL", "FOUR", "FREE", "FROM",
                "FUEL", "FULL", "FUND", "FURY", "FUSE", "FUSS", "GAIN",
                "GALA", "GALE", "GAME", "GANG", "GAPE", "GARB", "GATE",
                "GAVE", "GAZE", "GEAR", "GENE", "GIFT", "GILD", "GIRL",
                "GIVE", "GLAD", "GLEE", "GLEN", "GLOW", "GLUE", "GOAT",
                "GOES", "GOLD", "GOLF", "GONE", "GOOD", "GORE", "GRAB",
                "GRAM", "GRAY", "GREW", "GREY", "GRID", "GRIM", "GRIN",
                "GRIP", "GRIT", "GROW", "GULF", "GURU", "GUST", "HACK",
                "HAIL", "HAIR", "HALE", "HALF", "HALL", "HALT", "HAND",
                "HANG", "HARE", "HARM", "HARP", "HASH", "HASTE","HATE",
                "HAUL", "HAVE", "HAWK", "HAZE", "HAZY", "HEAD", "HEAL",
                "HEAP", "HEAR", "HEAT", "HEED", "HEEL", "HELD", "HELL",
                "HELM", "HELP", "HERD", "HERE", "HERO", "HIDE", "HIGH",
                "HIKE", "HILL", "HILT", "HIND", "HINT", "HIRE", "HOLD",
                "HOLE", "HOME", "HONE", "HOOD", "HOOK", "HOPE", "HORN",
                "HOSE", "HOST", "HOUR", "HUGE", "HULL", "HUNG", "HUNT",
                "HURL", "HURT", "HUSH", "HYMN", "HYPE", "ICON", "IDEA",
                "IDLE", "INCH", "INTO", "IRON", "ISLE", "ITEM", "JACK",
                "JADE", "JAIL", "JAMS", "JARS", "JAZZ", "JEAN", "JERK",
                "JEST", "JOBS", "JOIN", "JOKE", "JOLT", "JUMP", "JUNE",
                "JURY", "JUST", "KEEN", "KEEP", "KELP", "KEPT", "KICK",
                "KILL", "KIND", "KING", "KISS", "KITE", "KNEE", "KNEW",
                "KNIT", "KNOB", "KNOT", "KNOW", "LACE", "LACK", "LACY",
                "LAID", "LAKE", "LAMB", "LAME", "LAMP", "LAND", "LANE",
                "LARD", "LARK", "LASH", "LASS", "LAST", "LATE", "LAWN",
                "LAZY", "LEAD", "LEAF", "LEAK", "LEAN", "LEAP", "LEFT",
                "LEND", "LENS", "LENT", "LESS", "LICK", "LIED", "LIES",
                "LIFE", "LIFT", "LIKE", "LILY", "LIMB", "LIME", "LIMP",
                "LINE", "LINK", "LION", "LIST", "LIVE", "LOAD", "LOAF",
                "LOAN", "LOCK", "LOFT", "LOGO", "LONE", "LONG", "LOOK",
                "LOOP", "LORD", "LORE", "LOSE", "LOSS", "LOST", "LOTS",
                "LOUD", "LOVE", "LUCK", "LUMP", "LURE", "LURK", "LUSH",
                "MACE", "MADE", "MAIL", "MAIN", "MAKE", "MALE", "MALL",
                "MALT", "MANE", "MANY", "MARE", "MARK", "MARS", "MASH",
                "MASK", "MASS", "MAST", "MATE", "MAZE", "MEAL", "MEAN",
                "MEAT", "MELD", "MELT", "MEMO", "MEND", "MENU", "MERE",
                "MESH", "MESS", "MILD", "MILE", "MILK", "MILL", "MIME",
                "MIND", "MINE", "MINT", "MISS", "MIST", "MITE", "MOAN",
                "MOAT", "MOCK", "MODE", "MOLD", "MOLE", "MOOD", "MOON",
                "MOOR", "MOPE", "MORE", "MOSS", "MOST", "MOTH", "MOVE",
                "MUCH", "MUCK", "MULE", "MUSE", "MUSH", "MUST", "MUTE",
                "MYTH", "NAIL", "NAME", "NAPE", "NAVY", "NEAR", "NEAT",
                "NECK", "NEED", "NEST", "NEWS", "NEXT", "NICE", "NICK",
                "NINE", "NODE", "NONE", "NOON", "NORM", "NOSE", "NOTE",
                "NOUN", "NUDE", "OATH", "OBEY", "ODDS", "OMEN", "OMIT",
                "ONCE", "ONLY", "ONTO", "OOZE", "OPEN", "ORAL", "ORCA",
                "OVEN", "OVER", "PACE", "PACK", "PAGE", "PAID", "PAIL",
                "PAIN", "PAIR", "PALE", "PALM", "PANE", "PARK", "PART",
                "PASS", "PAST", "PATH", "PAVE", "PAWN", "PEAK", "PEAR",
                "PEAT", "PECK", "PEEK", "PEEL", "PEER", "PELT", "PERK",
                "PEST", "PICK", "PIER", "PILE", "PILL", "PINE", "PINK",
                "PIPE", "PITY", "PLAN", "PLAY", "PLEA", "PLOD", "PLOT",
                "PLOW", "PLOY", "PLUG", "PLUM", "PLUS", "POEM", "POET",
                "POLE", "POLL", "POLO", "POND", "PONY", "POOL", "POOR",
                "POPE", "PORK", "PORT", "POSE", "POST", "POUR", "PRAY",
                "PREP", "PREY", "PRIM", "PROD", "PROP", "PROS", "PULL",
                "PULP", "PUMP", "PUNK", "PURE", "PUSH", "QUIT", "QUIZ",
                "RACE", "RACK", "RAFT", "RAGE", "RAID", "RAIL", "RAIN",
                "RAKE", "RAMP", "RANG", "RANK", "RANT", "RARE", "RASH",
                "RATE", "RAVE", "READ", "REAL", "REAP", "REAR", "REED",
                "REEF", "REEL", "REIN", "RELY", "REND", "RENT", "REST",
                "RICH", "RIDE", "RIFT", "RING", "RIOT", "RIPE", "RISE",
                "RISK", "ROAD", "ROAM", "ROAR", "ROBE", "ROCK", "RODE",
                "ROLE", "ROLL", "ROOF", "ROOM", "ROOT", "ROPE", "ROSE",
                "RUDE", "RUIN", "RULE", "RUNG", "RUSE", "RUSH", "RUST",
                "SACK", "SAFE", "SAGE", "SAID", "SAIL", "SAKE", "SALE",
                "SALT", "SAME", "SAND", "SANE", "SANG", "SANK", "SAVE",
                "SCAN", "SCAR", "SEAL", "SEAM", "SEAT", "SEED", "SEEK",
                "SEEM", "SEEN", "SELF", "SELL", "SEND", "SENT", "SHED",
                "SHIN", "SHIP", "SHOP", "SHOT", "SHOW", "SHUT", "SICK",
                "SIDE", "SIFT", "SIGH", "SIGN", "SILK", "SING", "SINK",
                "SITE", "SIZE", "SKIP", "SLAB", "SLAG", "SLAM", "SLAP",
                "SLAT", "SLED", "SLEW", "SLID", "SLIM", "SLIP", "SLIT",
                "SLOT", "SLOW", "SLUG", "SLUM", "SNAP", "SNAG", "SNIP",
                "SNOW", "SNUB", "SOAK", "SOAP", "SOAR", "SOCK", "SODA",
                "SOFA", "SOFT", "SOIL", "SOLD", "SOLE", "SOME", "SONG",
                "SOON", "SORE", "SORT", "SOUL", "SOUR", "SPAN", "SPAR",
                "SPEC", "SPED", "SPIN", "SPIT", "SPOT", "SPUR", "STAB",
                "STAG", "STAR", "STAY", "STEM", "STEP", "STEW", "STIR",
                "STOP", "STUB", "STUD", "STUN", "SUCH", "SUIT", "SULK",
                "SUNG", "SUNK", "SURE", "SURF", "SWAN", "SWAP", "SWAY",
                "SWIM", "SWAM",
                "TABS", "TACK", "TACT", "TAIL", "TAKE", "TALE", "TALK",
                "TALL", "TAME", "TANK", "TAPE", "TAPS", "TART", "TASK",
                "TEAM", "TEAR", "TEEM", "TELL", "TEND", "TENS", "TENT",
                "TERM", "TEST", "TEXT", "THAN", "THAT", "THEM", "THEN",
                "THEY", "THIN", "THIS", "THUS", "TICK", "TIDE", "TIDY",
                "TIED", "TIER", "TILE", "TILL", "TILT", "TIME", "TINY",
                "TIPS", "TIRE", "TOAD", "TOIL", "TOLD", "TOLL", "TOMB",
                "TOME", "TONE", "TOOK", "TOOL", "TOPS", "TORE", "TORN",
                "TOSS", "TOUR", "TOWN", "TRAP", "TRAY", "TREE", "TREK",
                "TRIM", "TRIO", "TRIP", "TROD", "TROT", "TRUE", "TUBE",
                "TUCK", "TUFT", "TULIP","TUNA", "TUNE", "TURF", "TURN",
                "TUSK", "TWIN", "TYPE",
                "UGLY", "UNDO", "UNIT", "UPON", "URGE", "USED", "USER",
                "VAIN", "VALE", "VANE", "VARY", "VASE", "VAST", "VEIL",
                "VEIN", "VENT", "VERB", "VERY", "VEST", "VETO", "VICE",
                "VIDE", "VIEW", "VINE", "VISA", "VOID", "VOLT", "VOTE",
                "WADE", "WAGE", "WAIL", "WAIT", "WAKE", "WALK", "WALL",
                "WAND", "WANT", "WARD", "WARM", "WARN", "WARP", "WART",
                "WARY", "WASH", "WAVE", "WAVY", "WAXY", "WEAK", "WEAR",
                "WEED", "WEEK", "WEEP", "WELD", "WELL", "WENT", "WERE",
                "WEST", "WHAT", "WHEN", "WHIM", "WHIP", "WHOM", "WICK",
                "WIDE", "WIFE", "WILD", "WILL", "WILT", "WILY", "WIMP",
                "WIND", "WINE", "WING", "WINK", "WIPE", "WIRE", "WISE",
                "WISH", "WITH", "WOKE", "WOLF", "WOOD", "WOOL", "WORD",
                "WORE", "WORK", "WORM", "WORN", "WOVE", "WRAP",
                "YANK", "YARD", "YARN", "YEAR", "YELL", "YOGA", "YOKE",
                "YOUR",
                "ZEAL", "ZERO", "ZEST", "ZINC", "ZONE", "ZOOM",
                }

    if w in common_4:
        return 0

    # --- Negative signals (increase score = more obscure) ---

    # 3-letter words not in common keep list
    if wlen == 3 and w not in COMMON_3_KEEP:
        score += 40

    # Exotic letter combos
    if has_double_vowel_exotic(w):
        score += 50

    if "Q" in w and "QU" not in w:
        score += 60

    if has_no_vowel(w):
        score += 70

    # Technical/scientific endings
    if TECHNICAL_ENDINGS.search(w):
        score += 45

    if SCRABBLE_ENDINGS.search(w):
        score += 40

    # Rare letter usage (high-point Scrabble letters in weird positions)
    rare_letters = sum(1 for c in w if c in "QXZJK")
    if rare_letters >= 2:
        score += 30

    # Unusual consonant clusters
    if has_triple_consonant_cluster(w):
        score += 25

    # Words with very uncommon bigrams
    uncommon_bigrams = ["BV", "BX", "CB", "CF", "CJ", "CV", "CX", "DX",
                        "FQ", "FX", "GQ", "GX", "HJ", "HK", "HQ", "HX",
                        "IH", "JB", "JC", "JD", "JF", "JG", "JH", "JK",
                        "JL", "JM", "JN", "JP", "JQ", "JR", "JS", "JT",
                        "JV", "JW", "JX", "JY", "JZ", "KQ", "KX", "KZ",
                        "LQ", "LX", "MJ", "MQ", "MX", "MZ", "PQ", "PX",
                        "QA", "QB", "QC", "QD", "QE", "QF", "QG", "QH",
                        "QI", "QJ", "QK", "QL", "QM", "QN", "QO", "QP",
                        "QR", "QS", "QT", "QV", "QW", "QX", "QY", "QZ",
                        "SX", "VB", "VC", "VD", "VF", "VG", "VH", "VJ",
                        "VK", "VM", "VN", "VP", "VQ", "VW", "VX", "VZ",
                        "WQ", "WX", "WZ", "XB", "XD", "XF", "XG", "XJ",
                        "XK", "XQ", "XV", "XW", "XZ", "YQ", "YX", "ZB",
                        "ZC", "ZD", "ZF", "ZG", "ZJ", "ZK", "ZN", "ZP",
                        "ZQ", "ZR", "ZS", "ZV", "ZW", "ZX"]
    for bg in uncommon_bigrams:
        if bg in w:
            score += 15
            break

    return min(score, 100)


# ─── MAIN FILTERING ─────────────────────────────────────────────────────────

core_words = []
extended_words = []
removed_from_core = []
removed_from_extended = []

for word in all_words:
    w = word.upper().strip()
    if not w:
        continue

    obscurity = score_obscurity(w)

    # Core: obscurity <= 30
    if obscurity <= 30:
        core_words.append(w)
        extended_words.append(w)
    # Extended: obscurity 31-55
    elif obscurity <= 55:
        extended_words.append(w)
        removed_from_core.append(f"{w}\t(score={obscurity})")
    # Full only: obscurity > 55
    else:
        removed_from_core.append(f"{w}\t(score={obscurity})")
        removed_from_extended.append(f"{w}\t(score={obscurity})")

# ─── Additional heuristic pass: remove words that LOOK obscure ──────────────

def extra_core_filter(word):
    """Additional removal pass for Core — catches things the scorer misses."""
    w = word.upper()

    # Remove obscure 3-letter words
    if len(w) == 3 and w in OBSCURE_3_REMOVE and w not in COMMON_3_KEEP:
        return True

    is_obs, reason = looks_obscure(w)
    if is_obs and w not in COMMON_3_KEEP:
        return True

    return False

def extra_extended_filter(word):
    """Additional removal pass for Extended."""
    w = word.upper()
    is_obs, reason = looks_very_obscure(w)
    return is_obs and w not in COMMON_3_KEEP

# Apply extra filters
core_final = []
for w in core_words:
    if extra_core_filter(w):
        removed_from_core.append(f"{w}\t(extra_filter)")
    else:
        core_final.append(w)

extended_final = []
for w in extended_words:
    if extra_extended_filter(w):
        removed_from_extended.append(f"{w}\t(extra_filter)")
    else:
        extended_final.append(w)

# Ensure core is subset of extended
for w in core_final:
    if w not in set(extended_final):
        extended_final.append(w)
extended_final = sorted(set(extended_final))

# ─── Write outputs ───────────────────────────────────────────────────────────

core_final = sorted(set(core_final))
removed_from_core = sorted(set(removed_from_core))
removed_from_extended = sorted(set(removed_from_extended))

with open(CORE_PATH, "w") as f:
    for w in core_final:
        f.write(w + "\n")

with open(EXTENDED_PATH, "w") as f:
    for w in extended_final:
        f.write(w + "\n")

with open(REMOVED_FROM_CORE_PATH, "w") as f:
    f.write(f"# Words removed from Core dictionary (kept in Extended or Full)\n")
    f.write(f"# Total removed: {len(removed_from_core)}\n")
    f.write(f"# Format: WORD\\t(reason)\n\n")
    for entry in removed_from_core:
        f.write(entry + "\n")

with open(REMOVED_FROM_EXTENDED_PATH, "w") as f:
    f.write(f"# Words removed from Extended dictionary (kept only in Full)\n")
    f.write(f"# Total removed: {len(removed_from_extended)}\n")
    f.write(f"# Format: WORD\\t(reason)\n\n")
    for entry in removed_from_extended:
        f.write(entry + "\n")

# ─── Report ──────────────────────────────────────────────────────────────────

print(f"\n{'='*60}")
print(f"DICTIONARY CURATION RESULTS")
print(f"{'='*60}")
print(f"Full dictionary:     {len(all_words):>6} words (unchanged)")
print(f"Extended dictionary: {len(extended_final):>6} words ({len(all_words) - len(extended_final)} removed)")
print(f"Core dictionary:     {len(core_final):>6} words ({len(all_words) - len(core_final)} removed)")
print(f"")
print(f"Removed from Core:     {len(removed_from_core):>6} entries")
print(f"Removed from Extended: {len(removed_from_extended):>6} entries")
print(f"")

# Length distribution
for tier_name, tier_words in [("Core", core_final), ("Extended", extended_final), ("Full", all_words)]:
    by_len = {}
    for w in tier_words:
        l = len(w)
        by_len[l] = by_len.get(l, 0) + 1
    dist = ", ".join(f"{l}L={by_len.get(l,0)}" for l in range(3, 8))
    print(f"  {tier_name:10}: {dist}")

print(f"\nFiles written:")
print(f"  {CORE_PATH}")
print(f"  {EXTENDED_PATH}")
print(f"  {REMOVED_FROM_CORE_PATH}")
print(f"  {REMOVED_FROM_EXTENDED_PATH}")

# Spot check: show some example removals
print(f"\n{'='*60}")
print(f"SAMPLE REMOVALS FROM CORE (first 30)")
print(f"{'='*60}")
for entry in removed_from_core[:30]:
    print(f"  {entry}")

print(f"\n{'='*60}")
print(f"SAMPLE WORDS KEPT IN CORE (random sample)")
print(f"{'='*60}")
import random
sample = random.sample(core_final, min(30, len(core_final)))
for w in sorted(sample):
    print(f"  {w}")
