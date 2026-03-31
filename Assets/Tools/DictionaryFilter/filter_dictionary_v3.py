#!/usr/bin/env python3
"""
Dictionary Curation v3 — Root-anchored Core, structural Extended
=================================================================
Core (~10-15k):  Common roots + standard inflections only
Extended (~35-40k): Core + structurally normal enable1 words
Full (~51k): Everything (enable1)

This gives us a Core that truly feels like "words normal people know".
"""

import os, re, random

SCRIPT_DIR = os.path.dirname(os.path.abspath(__file__))
FULL_PATH = os.path.join(SCRIPT_DIR, "dict_full.txt")
CORE_PATH = os.path.join(SCRIPT_DIR, "dict_core.txt")
EXTENDED_PATH = os.path.join(SCRIPT_DIR, "dict_extended.txt")
REMOVED_CORE_PATH = os.path.join(SCRIPT_DIR, "dict_removed_from_core.txt")
REMOVED_EXT_PATH = os.path.join(SCRIPT_DIR, "dict_removed_from_extended.txt")
FORCE_INCLUDE_PATH = os.path.join(SCRIPT_DIR, "dict_force_include.txt")
FORCE_EXCLUDE_PATH = os.path.join(SCRIPT_DIR, "dict_force_exclude.txt")

with open(FULL_PATH) as f:
    full_set = set(line.strip().upper() for line in f if line.strip())
full_list = sorted(full_set)
print(f"Full: {len(full_list)} words")

# ═══════════════════════════════════════════════════════════════════════════════
# CORE: common English words that a normal player would recognize in ~2 seconds
# ═══════════════════════════════════════════════════════════════════════════════

# Root words — these are words the average English speaker KNOWS.
# Curated for gameplay: biased toward short, playable, recognizable words.
# Includes common nouns, verbs, adjectives, adverbs, and a few function words.

ROOTS = set(w.upper() for w in """
abort abrupt absent absorb abstract absurd abuse accent access accord account
accuse ache acid acorn acre actress acute adapt addict address adjust admiral
admire admit adobe adore adult advent adverb advice aerial affair afford
agency agenda agony ailment airline alarm album alcohol alert algae algebra
alibi alien alive allergy alley alligator allow allowance ally almanac alone
alphabet altar altitude alumni amateur amaze amber ambition ambulance ample
anchor ancient anecdote angel ankle annoy antenna anthem antique anxiety
apart apology appeal applaud apple appliance approval apricot apron aquarium
archery archive arctic arena argue armor aroma arrow arson artery article
ashamed aside aspect asphalt assault asset assume assure atlas attic auction
audio auditor aunt autumn avalanche avenue avoid awake award awning awry axle
babel baboon backbone backfire backpack backyard bacon badge badger badly
baffle bagel baggage bagpipe bailiff bakery balance balcony bald bale ballad
balloon ballot bamboo banana bandage bandit bane banish banjo banner baptism
barb barbecue bare barely bargain bark barley barn baron barrack barrage
barrel barrier basement basin basket bass baste batch bath bathe baton
battery bazaar beacon bead beak beam bearing beast beckon beehive beetle
beggar begun behave belly beloved bench beneath benefit berry besides bestow
betray betting beverage beyond bible bicycle billion bingo biology birch
birth biscuit bishop blade blame blanch blank blanket blaze bleach bleed
blend bless blind blink bliss blister blizzard bloat blob blockade blonde
blood bloom blossom blouse blueprint blunder blur blurt blush boar boast
boatman bodily bogus boil bolt bonfire bonnet bookcase bookmark boost booth
bore boring borough bosom boss bother boulder bounce bounty bouquet bourbon
bow bowling boycott brace bracelet bracket braid brain brake brand brandy
brass brave brazier breach breadth breakup breast breathe breeder breeze brew
brewery bribe bricklayer bride briefcase brigade brigand brink brisk brittle
broccoli brochure broil bromide bronco bronze brooch brook broth brownie
browse bruise bruiser brunch brunette brush brute bubble bucket buckle
buckwheat buddy budge budget buffalo bugle bulge bulk bullet bully bumble
bump bunch bundle bunker buoy burden bureau burglar burial burner burrito
burst bushel bustle busy butcher butler butt buttress buzzard bypass cabbage
cabin cabinet cable caboose cactus cadet café cage calcium calendar calf
caliber calm calorie camel camouflage campaign campfire campus canal canary
candle canopy canteen canton canvas canyon cape captain caption captive
caravan carbon cardiac cardinal career caretaker cargo carnival carpet carrot
carton cartoon carve cascade cashew cassette castle catalog catapult catch
category cattle caution cavalry ceiling celery cellar cement cemetery censure
census century ceramic cereal ceremony certain chain chalk chamber champion
channel chant chapel chapter charm chart charter chase chassis cheap cheat
chemist cherish cherry cherub chess chest chicken chime chimney chin chisel
choir choke chore chosen chunk cider cigar cinema circle circuit citizen
civilian clam clamp clan clap clarify clarinet clarity clash clasp classic
clause claw clean cleanse cleave clergy clerk clever cliff climate cling
clinic clip cloak clock clone cloth clown cluster clutch coarse coastal
cobalt cobble cocaine cockpit coconut coffin collage collar colonel colony
column combat combine comedy comet comfort comma commit compact compare
compass compel complex comply compose compute comrade conceal concern condemn
conduct confess confine confirm confront conquer consent consist console
consult consume contain content contest context contour control convert
convey convict copper coral cordial corner coroner corpse correct corrode
corrupt costume cottage cotton couch council counsel counter country county
courage cousin cozy cracker cradle cranberry crane crater crawl crease create
creator creature creek crest cricket crimson crisis crisp critter crooked
croquet crucial crystal cuddle cudgel cuisine culture cunning cupboard curb
cure curfew curious current curry curtain cushion custody custom cycle
cyclone cylinder daffodil dagger dairy daisy damage damp dangle daring
darling dart dazzle debate debris debtor decade deceit decent declare decline
decor decorate decoy decree dedicate deduct deed deepen default defeat defend
deficit define defy degree deity delay delete delicate delight deliver delta
demand demon denial dense dental deny depart depend deploy deposit depot
depress deprive deputy derive descend descent deserve design desire desktop
despair despise despite dessert destiny destroy detach detect detour device
devote devour dialect dialog diamond diaper diesel digest dilemma dilute
dimple dine diploma dire disable disarm discard disco discuss disease
disgrace disgust dismiss display dispose dispute dissolve distant distort
disturb divine dizzy doctrine dollar dolphin dominate donate donkey donor
doorway dormant dosage double dough drape drapery drastic drawer dread
dresser drift drizzle drought drowsy drum dual dubious duck duel duke dull
dungeon durable during dwell dwindle dynamic dynasty eager eagle earn earnest
eastern echo eclipse ecology economy edition editor educate effort eighth
elastic elbow elderly elegant element elevate eleven elicit eligible elite
ellipse embark embrace emerald emerge emigrate emotion emperor empire employ
empress enable enchant enclose encounter endless endorse endure enforce
engage engine engrave enhance enjoy enlarge enormous enquire enrich ensign
ensure entice entire entitle entry envelop envy episode equal equator equip
erosion errand escort essay essence estate eternal ethics ethnic evaluate
even evident evolve exalt examine example exceed excerpt excess exchange
excite exclaim exclude execute exempt exhaust exhibit exile exotic expand
expense explain explode exploit explore export expose extend extinct extract
extreme eyebrow fable fabric facial factory faculty failure fairly falcon
famine fanatic fantasy farewell fascinate fashion fasten fatal fatigue faucet
feast feather feature federal feeble fellow female fertile fervent festive
fiction fiddle fiery fifteen figment filament filter finale finance fireman
fiscal fission fixture fizzy flannel flatter flavor flaw fleece flicker
flight fling flip flock florist flounder flourish fluency fluffy fluids
flurry flutter focal focus folder folly fondle foolish forbid foreign foreman
forfeit forge forgery formula forsake fortify fortune fossil foster founder
fountain fraction fracture fragile fragment fragrant frantic freight frenzy
freshen friction fringe frolic frontier frost frozen frugal fulfill fumble
funeral fungus funnel furnace furnish fusion futile fuzzy gadget galaxy
gallop gamble gamer gangster garbage garden garlic garment garnish gasp
gateway gather gauge gazette gender general genetic genius gentle genuine
gesture gigantic glacier glamour glance glare gleam glide glimmer glimpse
glitter global globule gloom glory glossy glucose glue gnaw gobble goblet
goddess golden goodness goodwill gorge gorgeous gorilla gossip gourmet govern
gown gradual grammar granite graphic grateful gratify grave gravel gravity
grease greedy grieve grill grim grizzly groan grocer groom groove grope gross
grove growl growth grudge grumble guard guardian guerrilla guide guilt guilty
guitar gullible gully gutter guzzle habit habitat haircut halfway hallmark
hallway hamlet hamster handbook handful handicap handkerchief handsome handy
hangar happen harbor harden hardly hardship hardwood harmony harness harvest
hasten hatred haven haystack hazard headband headline healthy hearth heatwave
heaven heavenly helmet helpful herald herbal heritage hermit heroic hesitate
hidden highway hilltop hinder hippo historic history hollow homemade homework
honest honesty horizon hormone hornet horror horseback hospice hostage
hostile hotel hound housewife housework howitzer hullabaloo humble humidity
humming humorous hunger hunter hurdle hurricane hustle hygiene hymn hyphen
hysteria iceberg icicle idealist ignite ignore illegal illusion image imagine
immense immune impact impair impart impeach implant implore import impose
impress impulse incident incline include income indeed indent index indoor
indulge infect infer inflate inform inhabit inherit initial inject inmate
innate inner inning inquire insect insert insight insist inspect inspire
install instant instill instinct instruct insult intact integer intend
intense intent interact interim intrude invade invent invest invite invoice
involve inward ironic irrigate irritate island isolate issued ivory janitor
javelin jealous jewelry jigsaw journey joyous jubilee juggle jumble jumper
junction jungle justice justify juvenile kaleidoscope kangaroo karate
keepsake kennel kernel kerosene kettle kidnap kindle kingdom kinship kiosk
kitchen kitten knapsack kneecap kneel knight label laborer lacquer ladder
ladle ladybug lagoon landfill landlord landmark landscape lantern lapel latch
lately lateral lattice laundry lavish lawsuit layout leather lecture leftover
legend leisure lemon length leopard lettuce level lever liberty license
lifelong ligament lilac limelight limestone limousine lineage linen linger
liquid listen literal litter lively lizard lobby lobster locate lockdown
lodge lofty lonely lonesome lookout loosen lottery lounge lovely loyal
luggage lullaby lumber lunar luxury machine machinery madam madness magazine
magic magnet magnetic magnify maiden maintain majesty majority malice mallet
mammal manage mandate maneuver mangle manifest mankind mansion manual maple
marble margin marine marital market marshal martial martyr marvel mascot
masquerade massive mast master mattress mature maximum meadow measure
mechanic medal meddle medical medium melody memoir memorial mention mentor
mercury merely merit mermaid merry message meteor method midday middle midget
midpoint migrate military million mimic mineral minimum ministry minor
minority miracle mirror mischief misery missile mission mistake mixture
mobile mockery moderate modest moisten moisture mold molecule moment monarch
monitor monopoly monster monthly monument morsel mortal mortar mortgage
mosque motion motto mound mourn movement muffin muffle mule multiply mumble
mundane murder muscle museum mustard muster mutual muzzle mystery nanny
napkin narrate narrow nasty native natural naval navigate navy nearby
necklace needle neglect nephew nervous nestle neutral newborn nickel nickname
nightfall nimble ninety noble nomad nominal nominate nonsense noodle normal
notable notion nourish novelty nuclear nugget nuptial nursery nurture nuzzle
nylon oasis obedient oblige obscure observe obvious occasion occupy octave
offense officer official offset olive ominous ongoing onion onset opaque
opera operate opinion oppose optimal option oracle orange orbit ordeal organ
organic orient origin ornate orphan outbid outcome outdoor outlaw outline
output outrage outside outward overall overcome overlap oversee owed oxide
oxygen oyster pacific package paddle pageant palace palate pamper pancake
pandemic panel panic panther pantry paradox parcel pardon parish parlor
parole parrot partial partner passage passion passive pastime pastor pasture
patent pathway patient patriot patron pattern pavement payback payment
payroll peacock peasant pebble peculiar pedal penalty pending penguin pension
perceive percent perfect perfume perhaps peril period permit persist persona
pertain petition phantom pilgrim pillar pillow pinch pinpoint pioneer pistol
pitcher pixel pizzazz plague plaster plateau platform platter playful plea
pleasant pledge plenty pliers plop plotter plumber plummet plunder plunge
pocket polished polite pollen pollute polygon pompous ponder pontiff popular
porcelain porch portion portray posture potent pottery poultry poverty powder
prairie precede precise predict prefer premier premium prepare present
preside presume pretend prevail prevent preview primary primate primer prism
privacy private proceed proclaim profile program project prolong promote
prompt pronoun prophet propose prosper protect protein protest proverb
provide provoke prudent publish pudding puddle pulpit punctual punish puppet
pursuit puzzle pyramid qualify quality quantum quarrel quarter query quest
quicker quiver quota rabbit racetrack racial racket radical rainbow rally
ramble rancher random ransom rapture rascal rattle ravine razor reality
realize rebel rebound receipt recital reckon reclaim recover recruit recycle
redeem reduce referee refine reflect reform refrain refresh refund regard
regime region regret regular rehearse reign reject rejoice relapse relate
release reliant relic relief relish reluct remain remark remedy remind
remnant remote removal render renew renewal rental repay repeal replace
replica report republic request require rescue resemble reserve reside resign
resist resolve resort respect restore restrain result resume retail retain
retire retreat retrieve return reunion reveal revenue reverse review revival
revolt revolve reward rhetoric rhythm ribbon ricochet riddle ridicule
rigorous ripple ritual rivalry robot robust rodent rogue romance roster
rotate rotten routine rowdy rubble ruffle rumble rupture rustic rustle
ruthless sadden saddle safari sailor salary salmon saloon salute salvage
sandal sandstone sanity sarcasm sardine satellite satire satisfy savage
savanna savior scandal scarce scarlet scatter scenery scholar science
scissors scooter scorch scornful scoundrel scrabble scramble scratch scribble
scripted scroll sculptor sculpture seagull season secret sector segment
select senate senior sensible sentence sentimental sequel serpent session
settle settler sever shabby shadow shallow shanty shatter shawl shelter
sheriff shield shingle shipment shopper shortage shortcut shoulder shutter
shuttle sibling sidebar sidewalk siege signal signify silence silicon similar
simplify sincere sincerely sinister skeleton skeptic skilled slander
slaughter sleek sleeper slender slippery slogan sluggish smolder smother
smuggle snippet snowball society socket soften solar solemn solitary solution
somber somehow somewhat soothe soprano sorcery souvenir spacious sparkle
spatial species specify specimen spectacle spectrum splendid sponsor
spontaneous spotter sprinkle squash squat squid squint stadium stagger
stagnate stainless stampede staple startle startup statesman station statue
stature stellar sterile stiffen stimulus stingy stipend stockade stomach
storage stranger strategy streak stubborn stumble sturdy subject sublime
submerge submit subtle subtract suburb succeed success suction suffice suffix
suggest suitable sultan summary summit sunrise sunset superficial supreme
surface surgeon surpass surplus survive suspect suspend sustain swallow
sweater swindle symptom synonym syrup tackle tactic tailor takeoff talent
tamper tangle tantalize tariff tavern tease tedious temple temptation tenant
tender tennis tension terminal terrace terrain terrific terrify textile
texture theater therapy thermal thorough thousand thrift throttle thwart
tidal tidbit tighten timber timid tinker tissue titanium title tobacco toddle
toddler token tolerate toolbar topple torment torpedo torrent torture tourist
tractor traffic trailer training traitor trampoline transit translate
transmit transport trapeze trauma traveler treason treasure treaty tremble
tribune tribute trigger trinity triple trivial trolley tropical troupe
trumpet trustee tumble turbine turmoil turnover turquoise turtle tutor
twinkle typhoon tyranny ulcer ultimate umbrella undergo undermine uniform
unique unite universe upbeat update upgrade uphold upright uproot upscale
upstream uptown uranium utensil utilize utmost utopia utter vacation vaccine
vagrant valiant valid valley valuable vampire vanilla vanish variety vendor
venture verdict verify version vertical vessel veteran vibrant victim
vigorous village villain vintage violate violet virtual visible vision visual
vivid vocal volcano voltage volume vortex voyage vulgar vulture waddle waffle
wager wail waistband wallet walnut wander warble warden wardrobe warfare
warrior wasteful watchful watershed weapon weary weather webinar weekday
welfare whack whatever whereabouts whimper whimsical whisker whistle
wholesale wicked widget widow wield wildlife willful winding windmill window
windshield windy winery wingspan wintry wireless wizard wobble workshop
worship wrangle wrapper wrath wreath wreck wrestle wrinkle yearbook yielding
yoga youthful zealous zenith zigzag zodiac zombie zoning able ace ache acid
acme acne acre act add adept admit adopt adore adult age aged agent agile ago
agony agree ahead aid aide aider aim air airy aisle alarm ale alert alias
alibi alien align alike alive allay alley allot allow alloy allude allure
ally almond aloft alone along aloof also altar alter always amaze amen amid
ample amuse anchor angel anger angle angry ankle annex annoy annual ant ante
anti anvil any apart ape apex appeal appear apple apply arc arch arctic area
arena argue arid arise ark arm armor army arose array arrest arrive arrow
arson art ash aside ask asleep asset assist assume assure atlas atom atone
attach attack attain attic aunt author avail avid avoid awake award aware
awful awl awry ax axe axle aye babble babel baby back bacon badge badly bag
bagel bait bake baker bald bale ball ballot ban banana band bane bang banjo
bank banner banquet bar barb bare barely bargain bark barn baron barrel
barren barrier base basement bash basic basin basket bass baste bat batch
bath bathe battle bay beach beacon bead beak beam bean bear beard beast beat
beauty beckon bed bee beef been beer beetle before beg began begin begun
behave behind being belief bell belly belong below belt bench bend bent berry
beside best bestow bet betray better between beyond bias bible bid big bike
bill billion bind bingo birch bird birth bishop bit bite bitter black blade
blame bland blank blanket blast blaze bleach bleak bleed blend bless blind
blink bliss blister bloat blob block bloke blonde blood bloom blossom blouse
blow blue bluff blunt blur blurt blush boar board boast boat body bog bogus
boil bold bolt bomb bond bone bonus book boom boost boot booth border bore
boring born borough bosom boss both bother bottle bottom bought boulder
bounce bound bounty bouquet bourbon bow bowl box boy brace bracket braid
brain brake branch brand brass brave breach bread break breast breath breed
breeze brew bribe brick bride bridge brief brigade bright brim bring brink
brisk brittle broad broil bronze brood brook broom broth brown browse bruise
brush brute bubble bucket buckle bud buddy budge budget buffalo bug bugle
build built bulge bulk bull bullet bully bumble bump bunch bundle burden
burger burial burn burner burst bury bus bush bustle busy but butler butt
butter button buy buzz buzzard cab cabin cable cactus cadet café cage cake
calcium calf caliber call calm came camel camp campus can canal canary cancel
candle candy cane canopy canton canvas canyon cap cape captain caption
captive capture car carbon card care career careful cargo carpet carrot carry
cart carton carve case cash cast castle casual cat catalog catch cattle
caught cause caution cave cease cedar ceiling cell cellar cement census cent
center cereal certain chain chair chalk chamber champion chance change
channel chant chaos chap chapel chapter charge charm chart chase chat cheap
cheat check cheek cheer cheese chef cherish cherry chess chest chew chick
chicken chief child chill chime chin chip chisel choice choir choke choose
chop chord chore chosen chunk church cider cigar cinema circle circuit cite
citizen city civil claim clam clamp clan clap clarify clarity clash clasp
class classic claw clay clean cleanse clear clerk clever click cliff climb
cling clinic clip cloak clock clone close closet cloth cloud clown club clue
clump cluster clutch coach coal coarse coast coat cobble code coffin coil
coin cold collar colony color column comb combat combine come comedy comet
comfort comic comma command commit common compact company compare compel
compete complex comply compose compute conceal concern conduct cone confess
confide confine confirm conflict confront confuse connect conquer consent
consider consist console constant consult contact contain content contest
context continue contract control convent convert convey convince cook cookie
cool cope copper copy coral cord core cork corn corner corpse correct corrode
corrupt cost costume cosy cottage cotton couch could council counsel count
counter country county couple courage course court cousin cover cow cozy crab
crack cradle craft cramp crane crash crate crater crave crawl crazy cream
crease create creature credit creek creep crest crew crib cricket crime crisp
critic crooked crop cross crouch crow crowd crown crude cruel crush cry
crystal cube cuddle cue cuisine cult cunning cup cupboard curb cure curious
curl current curry curse curtain curve cushion custom cut cute cycle cylinder
dab dad dagger daily dairy daisy damage damp dance dangle dare daring dark
darling darn dart dash data date dawn day dazzle dead deaf deal dear death
debate debris debt decade decay deceit decent decide deck declare decline
decor decoy decree dedicate deed deem deep deepen deer default defeat defect
defend deficit define defy degree deity delay delete delicate delight deliver
delta demand demon denial dense dental deny depart depend depict deploy
deposit depot depress deprive depth deputy derive descend desert deserve
design desire desk despair despite dessert destiny destroy detach detail
detect deter develop device devil devote devour dial dialect dialog diamond
dice did die diesel diet differ dig digest digital dilemma dilute dim dimple
dine dinner dip diploma dire direct dirt dirty disable disagree discard disco
discover discuss disease dish disk dislike dismiss display dispose dispute
dissolve distance distant distinct district disturb ditch dive divine dizzy
dock doctor dodge does dog dollar dolphin dome donate done donkey donor doom
door doorway dose double doubt dough dove down draft drag dragon drain drake
drama drank drape drastic draw drawer dread dream dress drew dried drift
drill drink drip drive drizzle drop drought drove drown drowsy drum drunk dry
dual dubious duck dude due duel dug duke dull dumb dump dune dung dungeon
dunk durable during dusk dust dusty duty dwell dwindle dye dying dynamic each
eager eagle ear earl early earn earnest earth ease east eastern easy eat echo
eclipse ecology economy edge edit edition editor educate effort egg ego eight
eighth either elbow elder elderly elect elegant element elephant elevate
eleven eligible elite else email embark embrace emerge emotion emperor empire
employ empress empty enable enchant enclose encounter encourage end endless
endorse endure enemy energy enforce engage engine enhance enjoy enormous
enough enrich ensure enter entire entitle entry envelop envelope envy episode
equal equip era erase erect erosion errand error erupt escape escort essay
essence estate eternal ethnic evaluate even evening event ever every evident
evil evolve exact exalt exam examine example exceed excel except excerpt
excess exchange excite exclude excuse execute exempt exercise exhaust exhibit
exile exist exit exotic expand expect expense expert explain explode exploit
explore export expose extend extent extinct extra extract extreme eye eyebrow
fable fabric face facet facial fact factor factory faculty fade fail faint
fair fairly fairy faith fake falcon fall false fame family famine fan fanatic
fancy fang fantasy far fare farewell farm farmer fascinate fashion fast
fasten fat fatal fate father fatigue faucet fault favor fear feast feat
feather feature fed federal fee feeble feed feel feet fell fellow felt female
fence ferry fertile fervent festival festive fetch fever few fiber fiction
fiddle field fierce fiery fifteen fight figment figure filament file fill
film filter final finale finance find fine finger finish fire fireman firm
first fiscal fish fission fist fit five fix fixture fizzy flag flame flannel
flap flash flat flatter flavor flaw fled flee fleece flesh flew flex flicker
flight fling flip float flock flood floor flop florist flounder flour
flourish flow flower flu fluffy fluid fluids flurry flush flute flutter fly
foam focal focus fog foil fold folder folk follow folly fond fondle food fool
foolish foot for forbid force foreign forest forever forge forgery forget
forgive fork form formal former formula forsake fort forth fortify fortune
forward fossil foster found founder fountain four fox fraction fragile
fragment fragrant frame frank frantic fraud free freeze freight frenzy
frequent fresh freshen friction friend fright fringe frog frolic from front
frontier frost frown froze frozen frugal fruit fuel fulfill full fumble fun
fund funeral fungus funnel funny fur furnish fury fuse fusion fuss futile
future fuzzy gadget gain gala galaxy gale gallery gallop gamble game gamer
gang gangster gap garage garbage garden garlic garment garnish gasp gate
gateway gather gauge gave gaze gazette gear gem gender general genius gentle
genuine gesture get ghost giant gift gifted gigantic giggle gild girl give
given glad glamour glance glare glass gleam glide glimmer glimpse glitter
global globe gloom glory gloss glossy glove glow glucose glue gnaw goal goat
gobble goblet god gold golden golf gone good goodness goose gorge gorgeous
gossip got govern gown grab grace grade gradual grain grammar grand granite
grant grape graphic grasp grass grateful grave gravel gravity gray graze
grease great greed greedy green greet grew grey grief grieve grill grim grin
grind grip groan groom groove grope gross ground group grove grow growl
growth grudge grumble guard guardian guess guest guide guilt guilty guitar
gulf gully gum gun guru gust gut gutter guy guzzle gym habit hack hail hair
haircut half halfway hall hallway halt hamlet hammer hamster hand handful
handle handsome handy hang happen happy harbor hard harden hardly hardship
harm harmony harness harp harsh harvest has haste hasten hat hate hatred haul
haunt have haven hawk hay haystack hazard haze hazy head headline heal health
healthy heap hear heart hearth heat heaven heavy hedge heel height held hell
hello helmet help helpful hem hen her herald herb herbal herd here hermit
hero heroic hesitate hid hidden hide high hike hill hilltop him hinder hint
hip hippo hire his historic hit hive hobby hoe hog hold hole hollow holy home
homework honest honesty honey honor hood hook hope horizon horn hornet horror
horse hospice host hostage hostile hot hotel hound hour house housework hover
how however howl hub hue hug huge human humble humming humor humorous hundred
hung hunger hunt hunter hurdle hurl hurry hurt hush hustle hut hygiene hymn
hype hyphen ice icicle icy idea ideal idle idol ignite ignore ill illegal
image imagine immense immune impact impair impart implant implore imply
import impose impress improve impulse inch incline include income increase
indent index indicate indoor indulge infant infer inflate inform inhabit
inherit initial inject inmate innate inner inning innocent input insect
insert inside insight insist inspect inspire install instant instead instill
instruct insult intact integer intend intense intent interest interim
interior internal into introduce invade invent invest invite invoice involve
inward iron ironic island isolate issue item ivory ivy jab jacket jade jail
jam janitor jar jaw jazz jealous jeans jeer jelly jerk jersey jet jewel jig
job jog join joke jolly jolt journal journey joy jubilee judge jug juggle
juice jump junction jungle junior jury just justice justify juvenile kangaroo
keen keep keg kept kernel kettle key kick kid kidnap kill kind kindle king
kingdom kinship kiosk kiss kit kitchen kite kitten knack knapsack knee kneel
knew knife knight knit knob knock knot know label labor laborer lace lack lad
ladder laden ladle lady lag lagoon laid lake lamb lame lamp land landfill
landlord landscape lane lantern lap lapel large lark lash lass last latch
late lately later lateral latter lattice laugh launch laundry lava lavish law
lawn lawsuit lawyer lay layer layout lazy lead leader leaf league leak lean
leap learn lease leash least leather leave lecture led left leg legend
leisure lemon lend length lens lent leopard less lesson let letter lettuce
level lever liberty library license lick lid lie life lift ligament light
like likely lilac lily limb lime limelight limit limp line linen linger link
lion lip liquid list listen lit literal litter little live lively liver
lizard load loaf loan lobby local locate lock lodge lofty log logic lone
lonely lonesome long look lookout loop loose loosen lord lore lose loss lost
lot lottery loud lounge love lovely lover low loyal luck luggage lumber lump
lunar lunch lure lurk lush luxury machine mad madam made madness magazine
magic magnet magnify maid maiden mail main majesty major make male malice
mall mallet malt mammal man manage mandate mane maneuver mangle mankind
manner manor mansion manual many map maple marble march margin marine mark
market marry marsh marshal martial martyr marvel mask masquerade mass massive
mast master match mate matter mattress mature maximum may mayor maze meadow
meal mean measure meat mechanic medal meddle media medical medium meet melody
melt member memo memoir memory men mend mental mention mentor menu mercury
mercy mere merely merge merit mermaid merry mesh mess message metal meteor
method midday middle midget might migrate mild mile milk mill million mimic
mind mine miner mineral minimum minor mint minute miracle mirror mischief
misery missile mission mist mistake mix mixture moan moat mob mobile mock
mockery mode model modern modest moist moisten mold molecule moment monarch
money monk monkey monster month monthly monument mood moon moral more morning
morsel mortal mortar mosque moss most motel moth mother motion motor motto
mound mount mountain mourn mouse mouth move movement movie much muck mud
muffin muffle mug mule mumble mundane murder muscle museum music must mustard
muster mute mutual muzzle mystery myth nab nag nail name nanny nap napkin
narrow nasty nation native natural nature naval navy near nearby neat neck
need needle negative neglect nephew nerve nervous nest nestle net network
neutral never new newborn news next nice nick nickel night nimble nine ninety
noble nod noise nomad nominal none nonsense noodle noon nor normal north nose
notable note nothing notice notion nourish novel novelty now nowhere nuclear
nude nugget number nuptial nurse nursery nurture nut nuzzle nylon oak oar
oasis oath obedient obey object oblige obscure observe obtain obvious
occasion occupy ocean octave odd odds off offense offer office officer offset
often oil okay old olive ominous omit once one onion online only onset onto
opaque open opera opinion oppose optimal option oracle orange orbit ordeal
order organ orient origin ornate orphan other ought ounce our out outbid
outcome outdoor outer outlaw outline output outrage outside outward oval oven
over overall overcome overlap oversee owe owed owl own owner oxide oxygen
oyster pace pacific pack package pad paddle page pageant paid pail pain paint
pair palace palate pale palm pamper pan pancake pandemic panel panic pant
panther pantry paper parade parcel pardon parent parish park parlor parole
parrot part partial partner party pass passage passion passive past paste
pastime pastor pasture patch patent path pathway patient patriot patrol
patron pattern pause pave pavement pay payback payment peace peach peacock
peak pear pearl peasant pebble peck peculiar pedal peek peel peer pen penalty
pencil pending penguin penny pension people pepper per perceive percent
perfect perform perfume perhaps peril period permit persist person persona
pertain pest pet petal petition phantom phase phone photo phrase physical
piano pick picture pie piece pig pile pilgrim pill pillar pillow pilot pin
pinch pine pink pioneer pipe pistol pit pitch pitcher pity pixel pizza
pizzazz place plague plain plan plane planet plank plant plaster plate
plateau platform platter play player playful plea plead pleasant please
pledge plenty pliers plop plot plotter plow pluck plug plum plumb plumber
plummet plump plunder plunge plus pocket poem poet point poison pole police
polish polished polite poll pollen pollute pollution polygon pompous pond
ponder pontiff pony pool poor pop popular porcelain porch pork port portion
portray pose position positive possess possible post posture pot potato
potent potential pottery poultry pound pour poverty powder power prairie
praise pray prayer precede precious precise predict prefer premier premium
prepare present preserve preside president press pressure presume pretend
pretty prevail prevent preview previous price pride priest primary primate
primer prince princess print prior prism prison privacy private prize problem
proceed process proclaim produce product profile profit program progress
project prolong promise promote prompt pronoun proof proper property prophet
propose prosper protect protein protest proud prove proverb provide provoke
prudent public publish pudding puddle pull pulp pulpit pulse pump punch
punctual punish punk pupil puppet purchase pure purple purpose purse pursue
pursuit push put puzzle pyramid qualify quality quantum quarrel quarter queen
query quest question quick quicker quiet quilt quit quite quiver quiz quota
quote rabbit race racetrack racial rack racket radar radical rage raid rail
rain rainbow raise rally ramble ramp ranch rancher random range rank ransom
rapid rapture rare rascal rash rate rather rattle ravine raw ray razor reach
react read reader ready real realize rear reason rebel rebound recall receipt
receive recent recipe recital reckon reclaim record recover recruit recycle
red redeem reduce reef reel refer referee refine reflect reform refresh
refund refuse regard regime region regret regular rehearse reign reject
relapse relate release relic relief relish reluct rely remain remark remedy
remember remind remnant remote removal remove render renew rent rental repair
repay repeal repeat replace replica reply report request require rescue
research resemble reserve reside resign resist resolve resort resource
respect respond rest restore restrain result resume retail retain retire
retreat retrieve return reunion reveal revenue reverse review revival revolt
revolve reward rhetoric rhythm rib ribbon rice rich rid riddle ride ridge
ridicule rifle right rigid rigorous rim ring rinse riot ripe ripple rise risk
ritual rival river road roam roar rob robe robot robust rock rod rode rodent
rogue role roll roman romance roof room root rope rose roster rot rotate
rotten rough round route routine row rowdy royal rub rubble rude ruffle rug
ruin rule rumble rumor run rupture rural rush rust rustic rustle rut ruthless
sack sacred sad sadden saddle safari safe said sail saint sake salad salary
sale salmon saloon salt salute salvage same sample sand sandal sane sang
sanity sank sap sarcasm sardine sat sauce savage save savior saw say scale
scan scandal scar scarce scatter scene scenery scent scholar school science
scissors scooter scorch score scornful scoundrel scout scrabble scramble
scrap scrape scratch scream screen scribble script scripted scroll sculpture
sea seagull seal search season seat second secret section sector secure see
seed seek seem seen segment seize select self sell senate send senior sense
sensible sentence sentimental separate sequel series serious serpent serve
service session set settle seven sever severe shabby shade shadow shaft shake
shall shallow shame shanty shape share sharp shatter shave shawl she shed
sheer sheet shelf shell shelter sheriff shield shift shine shingle ship
shipment shirt shock shoe shoot shop shopper shore short shortage shortcut
shot should shoulder shout shove show shower shut shutter shuttle shy sibling
sick side sidebar siege sift sigh sight sign signal signify silence silent
silicon silk silly silver similar simple simplify since sincere sing single
sinister sink sir sister sit site situation six size skate skeleton skeptic
sketch ski skill skilled skin skip skirt skull sky slab slam slander slap
slate slave sleek sleep slender slice slick slide slight slim sling slip
slippery slit slogan slope slow slug sluggish slum smart smash smell smile
smoke smolder smooth smother smuggle snag snap snatch sneak snippet snow
snowball soak soap soar social society sock socket soft soften soil solar
sold soldier solemn solid solve somber some somehow somewhat son song soon
soothe soprano sorcery sore sorry sort soul sound soup source south souvenir
space spacious spare spark sparkle spatial speak special species specific
specify specimen spectacle spectrum speech speed spell spend sphere spice
spider spike spin spirit splash split spoken sponsor spontaneous spoon sport
spot spotter spread spring sprinkle squad square squash squat squeeze squid
squint stable stadium staff stage stagger stagnate stain stainless stair
stake stale stall stamp stand standard staple stare start startle startup
state statesman station statue status stay steady steak steal steam steel
steep steer stellar stem step sterile stern stick stiff stiffen still
stimulus sting stingy stipend stir stock stomach stone stood stool stop
storage store storm story stout stove straight strange stranger strap
strategy straw streak stream street strength stress stretch strict stride
strike string strip stripe stroke strong struck structure struggle stubborn
stuck student studio study stuff stumble stupid sturdy style subject sublime
submerge submit subtle subtract suburb succeed success such suction sudden
suffer suffice suffix sugar suggest suit suitable suite sultan sum summary
summer summit sun sunrise sunset super superficial supper supply support
suppose supreme sure surface surge surgeon surpass surplus surprise surround
survey survive suspect suspend sustain swallow swamp swap swarm sway swear
sweat sweater sweep sweet swell swept swift swim swindle swing switch sword
swore symbol sympathy symptom synonym syrup system table tack tackle tactic
tail tailor take takeoff tale talent talk tall tame tamper tan tangle tank
tantalize tap tape target tariff task taste taught tavern tax tea teach team
tear tease technical tedious temple tempt temptation ten tenant tend tender
tennis tense tension tent term terminal terms terrace terrain terrible
terrific terrify terror test text textile texture than thank that the theater
their them theme then theory therapy there thermal thick thief thin thing
think third thirst this thorn thorough those though thought thousand thread
threat three threw thrift thrill thrive throat throne throttle through throw
thrust thumb thunder thwart tick ticket tidal tidbit tide tidy tie tiger
tight tighten tile till timber time timid tin tinker tiny tip tire tissue
titanium title toast tobacco today toddle toddler toe together token tolerate
toll tomb tomorrow tone tongue tonight too tool toolbar tooth top topic
topple torch tore torment torn torpedo torrent torture total touch tough tour
tourist toward tower town trace track tractor trade tradition traffic trail
trailer train training trait traitor trampoline transit translate transmit
transport trap trapeze trash trauma travel traveler tray treason treasure
treat treaty tree tremble trend trial tribe tribune tribute trick tried
trigger trim trinity trio trip triple triumph trivial trod trolley troop
trophy tropical trouble troupe trout truck true truly trumpet trunk trust
trustee truth try tube tuck tuesday tug tulip tumble tumor tune tunnel
turbine turkey turmoil turn turnover turquoise turtle tutor twelve twenty
twice twin twinkle twist two type typhoon typical tyranny ugly ulcer ultimate
umbrella unable uncle under undergo undermine uniform unique unit unite
universe until unusual up upbeat update upgrade uphold upon upper upright
uproot upscale upset upstream uptown uranium urban urge urgent us use used
useful user usual utensil utility utilize utmost utopia utter vacant vacation
vaccine vagrant valiant valid valley valuable value vampire van vanilla
vanish vapor variety various vary vast vault vegetable vehicle vein velvet
vendor venture verb verdict verify version vertical very vessel veteran
vibrant vibrate vice victim victory video view vigorous village villain vine
vintage violate violence violet virtual virtue visible vision visit visitor
visual vital vivid vocal voice volcano voltage volume volunteer vortex vote
voyage vulgar vulture waddle wade waffle wage wager wagon wail waistband wait
wake walk wall wallet walnut wander want war warble ward warden wardrobe
warfare warm warn warp warrior wash waste wasteful watch watchful water
watershed wave wax way weak wealth weapon wear weary weather web webinar
website wedding weed week weekday weigh weight welcome welfare well west
western wet whack whale what whatever wheat wheel when where whereabouts
which while whimper whimsical whip whisker whisper whistle white whole
wholesale whom whose wicked wide widget widow wield wife wild wildlife will
willful willing win wind winding windmill window windshield windy wine winery
wing wingspan winner winter wintry wire wireless wisdom wise wish witch
within without witness wizard wobble woke wolf woman wonder wood wool word
wore work worker workshop world worm worry worse worship worst worth worthy
would wound wrangle wrap wrapper wrath wreath wreck wrestle wrinkle wrist
write writer wrong wrote yacht yank yard yarn yawn year yearbook yell yellow
yes yesterday yet yield yielding yoga you young your youth youthful zeal
zealous zenith zero zest zigzag zinc zodiac zombie zone zoning zoo zoom
""".split())

print(f"Root words: {len(ROOTS)}")

# ─── Generate inflections ────────────────────────────────────────────────────

def inflect(root):
    forms = {root}
    r = root.upper()
    n = len(r)
    if n > 7: return forms

    def add_if_valid(form):
        if 3 <= len(form) <= 7 and form in full_set:
            forms.add(form)

    # -S
    if r.endswith(("S", "X", "Z", "CH", "SH")):
        add_if_valid(r + "ES")
    elif r.endswith("Y") and n > 1 and r[-2] not in "AEIOU":
        add_if_valid(r[:-1] + "IES")
    elif r.endswith("FE"):
        add_if_valid(r[:-2] + "VES")
    elif r.endswith("F") and not r.endswith("FF"):
        add_if_valid(r[:-1] + "VES")
        add_if_valid(r + "S")
    else:
        add_if_valid(r + "S")

    # -ED
    if r.endswith("E"):
        add_if_valid(r + "D")
    elif r.endswith("Y") and n > 1 and r[-2] not in "AEIOU":
        add_if_valid(r[:-1] + "IED")
    elif n >= 3 and r[-1] not in "AEIOUWXY" and r[-2] in "AEIOU" and r[-3] not in "AEIOU":
        add_if_valid(r + r[-1] + "ED")
        add_if_valid(r + "ED")
    else:
        add_if_valid(r + "ED")

    # -ING
    if r.endswith("IE"):
        add_if_valid(r[:-2] + "YING")
    elif r.endswith("E") and not r.endswith("EE"):
        add_if_valid(r[:-1] + "ING")
    elif n >= 3 and r[-1] not in "AEIOUWXY" and r[-2] in "AEIOU" and r[-3] not in "AEIOU":
        add_if_valid(r + r[-1] + "ING")
        add_if_valid(r + "ING")
    else:
        add_if_valid(r + "ING")

    # -ER (agent/comparative)
    if r.endswith("E"):
        add_if_valid(r + "R")
    elif r.endswith("Y") and n > 1 and r[-2] not in "AEIOU":
        add_if_valid(r[:-1] + "IER")
    else:
        add_if_valid(r + "ER")

    # -EST
    if r.endswith("E"):
        add_if_valid(r + "ST")
    elif r.endswith("Y") and n > 1 and r[-2] not in "AEIOU":
        add_if_valid(r[:-1] + "IEST")
    else:
        add_if_valid(r + "EST")

    # -LY
    if r.endswith("LE"):
        add_if_valid(r[:-1] + "Y")
    elif r.endswith("Y"):
        add_if_valid(r[:-1] + "ILY")
    elif r.endswith("IC"):
        add_if_valid(r + "ALLY")
    else:
        add_if_valid(r + "LY")

    # -NESS
    if r.endswith("Y") and n > 1 and r[-2] not in "AEIOU":
        add_if_valid(r[:-1] + "INESS")
    else:
        add_if_valid(r + "NESS")

    # -MENT
    add_if_valid(r + "MENT")

    # -ABLE/-IBLE
    if r.endswith("E"):
        add_if_valid(r[:-1] + "ABLE")
    else:
        add_if_valid(r + "ABLE")

    # -TION/-SION (only add if in dictionary)
    if r.endswith("ATE"):
        add_if_valid(r[:-3] + "ATION")
    if r.endswith("T"):
        add_if_valid(r + "ION")

    # -EN
    add_if_valid(r + "EN")
    add_if_valid(r + "ENS")

    # -ISH
    add_if_valid(r + "ISH")

    # -FUL
    add_if_valid(r + "FUL")

    # -LESS
    add_if_valid(r + "LESS")

    # -OUS
    add_if_valid(r + "OUS")

    # un- prefix
    add_if_valid("UN" + r)

    # re- prefix
    add_if_valid("RE" + r)

    # -MAN/-MEN
    add_if_valid(r + "MAN")
    add_if_valid(r + "MEN")

    return forms

# Build core from roots
core_set = set()
for root in ROOTS:
    if root in full_set:
        core_set.add(root)
    for form in inflect(root):
        core_set.add(form)

# Also add some obvious words that inflection might miss
# (irregular forms, common compounds, etc.)
EXTRAS = set(w.upper() for w in """
am an as at be by do go he if in is it me my no of oh ok on or so to up us we
been being does doing goes going gone had has have having his its our she the
them they was were who you
able about above across after again along also another any are away back been
before being below best both bring came can could day did does done down each
even every face feel find first five four from gave get give goes gone good got
great had has have help here high home house into its just keep kind know last
left let life like line live long look made make many more most much must name
need new next nine none only open other our over own part past play put quite
read real rest right room run said same seem self show side some such sure take
tell than that them then they this time turn upon used very want well went were
what when will with word work year your
ate broken built burnt chose chosen come cut dealt done drawn driven drove eaten
fallen felt flew flown forgotten fought found gave given gone grew grown heard
held hidden hit hung kept knew known laid led left lent let lit lost made meant
met paid ran rode rung said sang sat saw seen sent set shook shown shut slept
slid spoke spoken stood struck swam swept swore sworn swum taken taught thought
threw thrown told took torn trod understood woke woken won wore worn wove
written wrote
children feet geese mice men oxen teeth women
bigger biggest hotter hottest sadder saddest thinner thinnest wetter wettest
""".split())

for w in EXTRAS:
    if w in full_set and 3 <= len(w) <= 7:
        core_set.add(w)

print(f"Core after roots + inflections + extras: {len(core_set)}")

# ═══════════════════════════════════════════════════════════════════════════════
# EXTENDED: Core + structurally normal words (catches most reasonable words)
# ═══════════════════════════════════════════════════════════════════════════════

def is_structurally_normal(word):
    w = word.upper()
    if not any(c in w for c in "AEIOUY"):
        return False
    if "Q" in w and "QU" not in w:
        return False
    if w.startswith("AA") or "UU" in w:
        return False
    if "II" in w:
        return False
    exotic = {"BH","BW","CW","DH","DZ","FJ","GH","HM","KH","MH","NH","NJ",
              "PF","SR","SV","VL","ZH","ZW","ZY"}
    if len(w) >= 2 and w[:2] in exotic:
        return False
    # Quad consonant cluster
    cons = "BCDFGHJKLMNPQRSTVWXZ"
    run = 0
    for c in w:
        run = run + 1 if c in cons else 0
        if run >= 4: return False
    return True

# Obscure 3-letter words to exclude from Extended too
# Only the truly obscure Scrabble-specialist 3-letter words.
# Common words like OLE, EWE, AWN, ORC, COG, etc removed from this list.
OBSCURE_3 = {
    "AAL","AAS","ABA","ABY","AGA","AGS","AHI","AIS",
    "AIT","ALA","ALB","AMA","AMU","ANA","ANE","ANI","APO","ARB",
    "ARD","ARF","ATT","AVA","AVO","AWA","AYS","AZO",
    "BAP","BEL","BES","BEY","BIS","BOS","BRR","BYS",
    "CEE","CEL","CEP","CIS","COR","COZ","CRU","CWM",
    "DAK","DAL","DAP","DAW","DEV","DEX","DEY","DIB","DIT","DOL","DOP","DOR","DOW","DUP",
    "ELD","ELS","EME","EMS","ENG","ENS","ERN","ERS","ESS","ETH",
    "FEH","FEM","FER","FES","FET","FEU","FEY","FID","FIS","FOH","FON","FOU","FOY","FUB","FUD","FUG",
    "GAE","GAN","GAR","GAT","GED","GEY","GHI","GID","GIE","GIP","GOS","GOX","GUL","GUP","GUS","GUV","GYP",
    "HAE","HAO","HAP","HEH","HES","HET","HIC","HIE","HIN","HUP",
    "ICH","IFF","IGG",
    "JEE","JIN","JOW","JUS",
    "KAB","KAE","KAF","KAS","KAT","KEA","KED","KEF","KEP","KEX","KHI","KIF","KIP","KIR","KIS","KOA","KOB","KOP","KOR","KOS","KUE",
    "LAC","LAT","LAV","LEK","LEU","LEV","LEY","LIS",
    "MAE","MAS","MAW","MEL","MEM","MHO","MIB","MIG","MIM","MIR","MIS","MOC","MOG","MOR","MOS","MUN","MUS","MUX",
    "NAE","NAM","NAW","NIM","NOB","NOG","NOM","NOO","NOS","NUS",
    "OBI","OCA","ODS","OES","OHO","OKA","OKE","OMS","ONO","ONS","OOT","OOZ","OPE","ORA","ORS","ORT","OSE","OUD","OXO","OXY",
    "PAC","PAM","PAS","PAX","PEC","PEH","PES","PHT","PIA","PIS","PIU","PIX","POH","POI","POM","PSI","PUL","PUR",
    "QAT","QIS","QUA",
    "RAI","RAX","REB","REC","REG","REI","REM","RES","RET","RHO","RIA","RIF","RIN","ROM",
    "SAB","SAE","SAU","SEG","SEI","SEL","SEN","SER","SHA","SHH","SIB","SIM","SKA","SOM","SUQ","SYN",
    "TAE","TAO","TAS","TAU","TAV","TAW","TEG","TET","TEW","THO","TIS","TOD","TUI","TUP","TWA",
    "UDO","UDS","ULU","UNS","UPO","URB","URD","URP","URS","UTA","UTS",
    "VAU","VAV","VAW","VIG","VUG",
    "WAB","WAE","WAP","WAT","WAW","WHA","WOS","WOT",
    "YAR","YEH","YOB","YOD","YOK","YOM","YON","YOW","YUK",
    "ZAX","ZEK","ZEP",
}

# Extended = everything structurally normal, minus the most obscure 3-letter words
extended_set = set()
for w in full_list:
    if is_structurally_normal(w):
        if len(w) == 3 and w in OBSCURE_3:
            continue
        extended_set.add(w)

# Ensure core is subset of extended
extended_set.update(core_set)

# Load force include/exclude
force_include = set()
force_exclude = set()
for path, target in [(FORCE_INCLUDE_PATH, force_include), (FORCE_EXCLUDE_PATH, force_exclude)]:
    if os.path.exists(path):
        with open(path) as f:
            for line in f:
                w = line.strip().upper()
                if w and not w.startswith("#"):
                    target.add(w)

core_set.update(w for w in force_include if w in full_set)
core_set -= force_exclude
extended_set.update(w for w in force_include if w in full_set)
extended_set -= force_exclude

# ─── Write ───────────────────────────────────────────────────────────────────

core_list = sorted(core_set)
extended_list = sorted(extended_set)

with open(CORE_PATH, "w") as f:
    for w in core_list: f.write(w + "\n")
with open(EXTENDED_PATH, "w") as f:
    for w in extended_list: f.write(w + "\n")

removed_core = sorted(full_set - core_set)
removed_ext = sorted(full_set - extended_set)

with open(REMOVED_CORE_PATH, "w") as f:
    f.write(f"# {len(removed_core)} words in Full but NOT in Core\n")
    f.write(f"# To add back, put in dict_force_include.txt\n\n")
    for w in removed_core: f.write(w + "\n")
with open(REMOVED_EXT_PATH, "w") as f:
    f.write(f"# {len(removed_ext)} words in Full but NOT in Extended\n\n")
    for w in removed_ext: f.write(w + "\n")

for path in [FORCE_INCLUDE_PATH, FORCE_EXCLUDE_PATH]:
    if not os.path.exists(path):
        with open(path, "w") as f:
            f.write("# One word per line (UPPERCASE). Lines starting with # are comments.\n")

# ─── Report ──────────────────────────────────────────────────────────────────

print(f"\n{'='*60}")
print(f"DICTIONARY TIERS")
print(f"{'='*60}")
print(f"  Full:     {len(full_list):>6} words (original enable1)")
print(f"  Extended: {len(extended_list):>6} words ({len(full_list)-len(extended_list):>5} removed)")
print(f"  Core:     {len(core_list):>6} words ({len(full_list)-len(core_list):>5} removed)")
print()

for name, words in [("Full", full_list), ("Extended", extended_list), ("Core", core_list)]:
    by_len = {}
    for w in words: by_len[len(w)] = by_len.get(len(w), 0) + 1
    dist = "  ".join(f"{l}L={by_len.get(l,0):>5}" for l in range(3, 8))
    print(f"  {name:10}: {dist}")

# Sample checks
print(f"\n{'='*60}")
print("CORE 3-letter words (all):")
print(f"{'='*60}")
c3 = sorted(w for w in core_list if len(w) == 3)
for i in range(0, len(c3), 20):
    print("  " + " ".join(c3[i:i+20]))

print(f"\n{'='*60}")
print("Random CORE sample (40 words):")
print(f"{'='*60}")
for w in sorted(random.sample(core_list, min(40, len(core_list)))):
    print(f"  {w}")

print(f"\n{'='*60}")
print("Words removed from Core (random 40):")
print(f"{'='*60}")
for w in sorted(random.sample(removed_core, min(40, len(removed_core)))):
    print(f"  {w}")
