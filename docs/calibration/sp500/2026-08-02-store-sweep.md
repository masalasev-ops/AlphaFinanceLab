# Store sweep — sp500, 2026-08-02 (D120)

*The stored corpus, audited by the CURRENT data-quality gate plus the two member-window detectors (findings 350/351). 1208 securities audited; store coverage floor 2001-07-24. Report-only: remediation is the operator adding the recommended symbols to `Universe:Exclusions` (finding 266's roster deny-list — the bars stay, the roster forgets them, rule 3).*

Thresholds: single-event dividend yield ≥ ×0.5; member 63-session median dollar volume < $1000000; gate bound ×10.

## Recommended for `Universe:Exclusions` — 39 securities

| symbol | id | impossible prints (R2) | member-volume breach windows | impossible dividend yields | worst evidence |
|---|---:|---:|---:|---:|---|
| `ACS` | 761 | 275 | 970 | 0 | 2007-11-26: Physically-impossible one-session dropout-and-revert: adjusted price ×0.0254 into 2007-11-26 then ×39.4 out (bound ×10) — a vendor bad print, not a real move (rule 10, fail closed). |
| `KRI` | 331 | 232 | 0 | 0 | 2008-05-22: Physically-impossible one-session dropout-and-revert: adjusted price ×0.08 into 2008-05-22 then ×12.2 out (bound ×10) — a vendor bad print, not a real move (rule 10, fail closed). |
| `SLR` | 612 | 139 | 378 | 0 | 2013-01-31: Physically-impossible one-session dropout-and-revert: adjusted price ×0.0983 into 2013-01-31 then ×10.2 out (bound ×10) — a vendor bad print, not a real move (rule 10, fail closed). |
| `TIN` | 480 | 85 | 166 | 1 | 2011-05-05: Physically-impossible one-session dropout-and-revert: adjusted price ×0.0943 into 2011-05-05 then ×11.8 out (bound ×10) — a vendor bad print, not a real move (rule 10, fail closed). |
| `CFC` | 558 | 60 | 435 | 1 | 2006-12-18: Physically-impossible one-session dropout-and-revert: adjusted price ×0.082 into 2006-12-18 then ×12.8 out (bound ×10) — a vendor bad print, not a real move (rule 10, fail closed). |
| `MEL` | 355 | 44 | 0 | 0 | 2014-07-21: Physically-impossible one-session dropout-and-revert: adjusted price ×6.86E-05 into 2014-07-21 then ×1.48E+04 out (bound ×10) — a vendor bad print, not a real move (rule 10, fail closed). |
| `SOV` | 765 | 22 | 713 | 0 | 2015-04-10: Physically-impossible one-session dropout-and-revert: adjusted price ×0.0943 into 2015-04-10 then ×10.6 out (bound ×10) — a vendor bad print, not a real move (rule 10, fail closed). |
| `PBG` | 712 | 20 | 36 | 0 | 2017-03-20: Physically-impossible one-session spike-and-revert: adjusted price ×23.8 into 2017-03-20 then ×0.044 out (bound ×10) — a vendor bad print, not a real move (rule 10, fail closed). |
| `CIN` | 192 | 20 | 1 | 0 | 2008-05-21: Physically-impossible one-session dropout-and-revert: adjusted price ×0.00574 into 2008-05-21 then ×174 out (bound ×10) — a vendor bad print, not a real move (rule 10, fail closed). |
| `NCC` | 374 | 7 | 0 | 0 | 2013-08-23: Physically-impossible one-session dropout-and-revert: adjusted price ×0.0929 into 2013-08-23 then ×12.4 out (bound ×10) — a vendor bad print, not a real move (rule 10, fail closed). |
| `HPC` | 297 | 6 | 181 | 0 | 2015-02-17: Physically-impossible one-session dropout-and-revert: adjusted price ×0.0138 into 2015-02-17 then ×72.5 out (bound ×10) — a vendor bad print, not a real move (rule 10, fail closed). |
| `GDW` | 264 | 4 | 127 | 0 | 2014-10-27: Physically-impossible one-session spike-and-revert: adjusted price ×7.83E+03 into 2014-10-27 then ×0.000128 out (bound ×10) — a vendor bad print, not a real move (rule 10, fail closed). |
| `WWY` | 526 | 4 | 0 | 0 | 2015-03-24: Physically-impossible one-session dropout-and-revert: adjusted price ×0.0786 into 2015-03-24 then ×12.7 out (bound ×10) — a vendor bad print, not a real move (rule 10, fail closed). |
| `GR` | 275 | 0 | 1593 | 0 | 63-session median $0 / day ending 2008-08-15 — an index member trades orders of magnitude more |
| `EP` | 641 | 0 | 1550 | 0 | 63-session median $0 / day ending 2007-10-03 — an index member trades orders of magnitude more |
| `AYE` | 696 | 0 | 1236 | 0 | 63-session median $0 / day ending 2006-04-03 — an index member trades orders of magnitude more |
| `CPWR` | 613 | 0 | 1218 | 0 | 63-session median $0 / day ending 2007-04-18 — an index member trades orders of magnitude more |
| `BBT` | 574 | 0 | 1086 | 0 | 63-session median $357059 / day ending 2006-04-05 — an index member trades orders of magnitude more |
| `GENZ` | 730 | 0 | 744 | 0 | 63-session median $7036 / day ending 2009-02-25 — an index member trades orders of magnitude more |
| `RX` | 549 | 0 | 672 | 0 | 63-session median $0 / day ending 2006-04-03 — an index member trades orders of magnitude more |
| `KG` | 684 | 0 | 601 | 0 | 63-session median $21595 / day ending 2008-08-06 — an index member trades orders of magnitude more |
| `BBBY` | 635 | 0 | 455 | 0 | 63-session median $578224 / day ending 2011-12-08 — an index member trades orders of magnitude more |
| `WEN` | 518 | 0 | 313 | 0 | 63-session median $383718 / day ending 2008-04-23 — an index member trades orders of magnitude more |
| `UVN` | 707 | 0 | 249 | 0 | 63-session median $7920 / day ending 2006-11-20 — an index member trades orders of magnitude more |
| `UIS` | 500 | 0 | 166 | 0 | 63-session median $669448 / day ending 2006-09-18 — an index member trades orders of magnitude more |
| `HNZ` | 294 | 0 | 159 | 29 | 63-session median $0 / day ending 2009-09-04 — an index member trades orders of magnitude more |
| `NYX` | 847 | 0 | 149 | 0 | 63-session median $0 / day ending 2009-10-21 — an index member trades orders of magnitude more |
| `HOT` | 690 | 0 | 108 | 0 | 63-session median $0 / day ending 2015-11-18 — an index member trades orders of magnitude more |
| `KMG` | 328 | 0 | 92 | 0 | 63-session median $8000 / day ending 2006-04-03 — an index member trades orders of magnitude more |
| `APCC` | 664 | 0 | 84 | 0 | 63-session median $136665 / day ending 2006-06-07 — an index member trades orders of magnitude more |
| `SII` | 809 | 0 | 70 | 0 | 63-session median $0 / day ending 2010-05-20 — an index member trades orders of magnitude more |
| `ODP` | 625 | 0 | 49 | 0 | 63-session median $809760 / day ending 2009-04-20 — an index member trades orders of magnitude more |
| `NBR` | 685 | 0 | 18 | 0 | 63-session median $980143 / day ending 2013-10-25 — an index member trades orders of magnitude more |
| `WY` | 527 | 0 | 0 | 1 | 2010-07-20: 26.42 per share on a 15.94 close = ×1.66 of price in ONE payout |
| `SSP` | 789 | 0 | 0 | 1 | 2008-07-01: 34.02839 per share on a 2.0081 close = ×16.95 of price in ONE payout |
| `FIS` | 813 | 0 | 0 | 1 | 2008-07-03: 16.5 per share on a 20.14 close = ×0.82 of price in ONE payout |
| `DISCA` | 909 | 0 | 0 | 1 | 2014-08-07: 39.00441 per share on a 40.975 close = ×0.95 of price in ONE payout |
| `DISCK` | 963 | 0 | 0 | 1 | 2014-08-07: 39.78 per share on a 39.78 close = ×1.00 of price in ONE payout |
| `NLOK` | 1084 | 0 | 0 | 1 | 2020-02-03: 12.0 per share on a 17.15 close = ×0.70 of price in ONE payout |

Paste-ready (append to the existing list — do NOT drop `SUN`):

```json
"Exclusions": [ "ACS", "KRI", "SLR", "TIN", "CFC", "MEL", "SOV", "PBG", "CIN", "NCC", "HPC", "GDW", "WWY", "GR", "EP", "AYE", "CPWR", "BBT", "GENZ", "RX", "KG", "BBBY", "WEN", "UVN", "UIS", "HNZ", "NYX", "HOT", "KMG", "APCC", "SII", "ODP", "NBR", "WY", "SSP", "FIS", "DISCA", "DISCK", "NLOK" ]
```

## Membership spells with no stored bars — 220 spell(s)

*Coverage, not exclusion: nothing was ingested, so there is nothing to quarantine. These are members the replay CANNOT price for the listed spell (the NCC shape: the vendor file is entirely a later recycled listing). The 7.9M `missing_bar` warns record this per-day; this is the per-security rollup.*

| symbol | id | bareless spells | also recommended for exclusion? |
|---|---:|---:|---|
| `AAMRQ` | 105 | 1 | no |
| `ABI` | 106 | 1 | no |
| `ABKFQ` | 695 | 1 | no |
| `ABS` | 107 | 1 | no |
| `ABX` | 108 | 1 | no |
| `ACV` | 110 | 1 | no |
| `ADCT` | 629 | 1 | no |
| `ADT` | 936 | 1 | no |
| `AGC` | 117 | 1 | no |
| `AL` | 122 | 1 | no |
| `ALTR` | 660 | 1 | no |
| `AM` | 124 | 1 | no |
| `ANRZQ` | 920 | 1 | no |
| `APC` | 563 | 1 | no |
| `ASO` | 616 | 1 | no |
| `AT` | 137 | 1 | no |
| `ATGE` | 895 | 1 | no |
| `AV` | 682 | 1 | no |
| `AWE` | 717 | 1 | no |
| `BEAM` | 149 | 1 | no |
| `BGEN` | 654 | 1 | no |
| `BR` | 168 | 2 | no |
| `BUD` | 171 | 1 | no |
| `BVSN` | 689 | 1 | no |
| `CA` | 172 | 1 | no |
| `CAM` | 856 | 1 | no |
| `CASY` | 1203 | 1 | no |
| `CBE` | 178 | 2 | no |
| `CBH` | 804 | 1 | no |
| `CDAY` | 1123 | 1 | no |
| `CE` | 710 | 1 | no |
| `CEG` | 184 | 2 | no |
| `CF` | 668 | 1 | no |
| `CHIR` | 692 | 1 | no |
| `CIEN` | 722 | 1 | no |
| `CNC` | 555 | 1 | no |
| `CNXT` | 655 | 1 | no |
| `COC-B` | 631 | 1 | no |
| `COHR` | 1199 | 1 | no |
| `CPNLQ` | 693 | 1 | no |
| `CPQ` | 203 | 1 | no |
| `CR` | 204 | 1 | no |
| `DALRQ` | 213 | 1 | no |
| `DELL` | 546 | 2 | no |
| `DG` | 593 | 2 | no |
| `DLX` | 222 | 1 | no |
| `DNB` | 879 | 1 | no |
| `DO` | 887 | 1 | no |
| `DOW` | 224 | 1 | no |
| `DPHIQ` | 621 | 1 | no |
| `DTV` | 818 | 1 | no |
| `DYN` | 683 | 1 | no |
| `EC` | 229 | 1 | no |
| `ECHO` | 1208 | 1 | no |
| `EHC` | 554 | 1 | no |
| `EMC` | 537 | 1 | no |
| `ENRNQ` | 238 | 1 | no |
| `EQ` | 802 | 1 | no |
| `EQT` | 881 | 2 | no |
| `ESV` | 821 | 2 | no |
| `ETS` | 242 | 1 | no |
| `FB` | 953 | 1 | no |
| `FBF` | 245 | 1 | no |
| `FDC` | 249 | 1 | no |
| `FDXF` | 1205 | 1 | no |
| `FISV` | 711 | 2 | no |
| `FLEX` | 1206 | 1 | no |
| `FMC` | 256 | 1 | no |
| `FRX` | 691 | 1 | no |
| `FSL` | 771 | 1 | no |
| `FSLR` | 901 | 2 | no |
| `G` | 261 | 1 | no |
| `GDT` | 551 | 1 | no |
| `GGP` | 835 | 2 | no |
| `GLK` | 269 | 1 | no |
| `GP` | 271 | 1 | no |
| `GPU` | 274 | 1 | no |
| `GX` | 634 | 1 | no |
| `H` | 283 | 1 | no |
| `HCA` | 286 | 2 | no |
| `HCP` | 857 | 1 | no |
| `HCR` | 600 | 1 | no |
| `HI` | 290 | 1 | no |
| `HLT` | 292 | 2 | no |
| `HM` | 293 | 1 | no |
| `HMA` | 728 | 1 | no |
| `IMNX` | 725 | 1 | no |
| `INCLF` | 308 | 1 | no |
| `INFO` | 1035 | 1 | no |
| `IR` | 312 | 1 | no |
| `JBL` | 706 | 2 | no |
| `JHF` | 715 | 1 | no |
| `JP` | 320 | 1 | no |
| `KDP` | 869 | 2 | no |
| `KM` | 326 | 1 | no |
| `KMI` | 699 | 2 | no |
| `LB` | 334 | 1 | no |
| `LDOS` | 903 | 2 | no |
| `LEHMQ` | 577 | 1 | no |
| `LIFE` | 878 | 1 | no |
| `LITE` | 1200 | 1 | no |
| `LLL` | 770 | 1 | no |
| `LU` | 547 | 1 | no |
| `MAY` | 347 | 1 | no |
| `MDR` | 352 | 1 | no |
| `MEA` | 353 | 1 | no |
| `MEDI` | 667 | 1 | no |
| `MEL` | 355 | 1 | yes |
| `MI` | 735 | 1 | no |
| `MIR` | 567 | 1 | no |
| `MMI` | 916 | 1 | no |
| `MNK` | 964 | 1 | no |
| `MON` | 747 | 1 | no |
| `MRSH` | 1198 | 1 | no |
| `MRVL` | 1207 | 1 | no |
| `MTLQQ` | 366 | 1 | no |
| `MXIM` | 663 | 2 | no |
| `NCC` | 374 | 1 | yes |
| `NE` | 705 | 2 | no |
| `NMK` | 377 | 1 | no |
| `NRTLQ` | 380 | 1 | no |
| `NSI` | 382 | 1 | no |
| `NSM` | 383 | 1 | no |
| `NXTL` | 582 | 1 | no |
| `OAT` | 388 | 1 | no |
| `OKE` | 389 | 1 | no |
| `ONE` | 392 | 1 | no |
| `PALM` | 679 | 1 | no |
| `PCG` | 401 | 2 | no |
| `PCL` | 733 | 1 | no |
| `PCS` | 610 | 1 | no |
| `PD` | 403 | 1 | no |
| `PDG` | 404 | 1 | no |
| `PEAK` | 1085 | 1 | no |
| `PGN` | 409 | 1 | no |
| `PHA` | 411 | 1 | no |
| `PLL` | 415 | 1 | no |
| `POM` | 850 | 1 | no |
| `PSFT` | 605 | 1 | no |
| `PTC` | 556 | 2 | no |
| `PVN` | 423 | 1 | no |
| `PWER` | 686 | 1 | no |
| `PX` | 424 | 1 | no |
| `Q` | 675 | 2 | no |
| `QTRN` | 646 | 1 | no |
| `RAL` | 428 | 1 | no |
| `RATL` | 734 | 1 | no |
| `RDS-A` | 432 | 1 | no |
| `RE` | 1038 | 1 | no |
| `RIG` | 651 | 2 | no |
| `RSHCQ` | 439 | 1 | no |
| `S` | 444 | 1 | no |
| `SAF` | 445 | 1 | no |
| `SAPE` | 661 | 1 | no |
| `SATS` | 1201 | 1 | no |
| `SDS` | 745 | 1 | no |
| `SE` | 820 | 1 | no |
| `SGP` | 451 | 1 | no |
| `SHLD` | 777 | 1 | no |
| `SNDK` | 800 | 2 | no |
| `SOTR` | 615 | 1 | no |
| `SPLS` | 606 | 1 | no |
| `STI` | 463 | 1 | no |
| `STR` | 817 | 1 | no |
| `SUNEQ` | 832 | 1 | no |
| `TE` | 727 | 1 | no |
| `TEK` | 474 | 1 | no |
| `TEL` | 836 | 2 | no |
| `TER` | 645 | 2 | no |
| `TKR` | 482 | 1 | no |
| `TNB` | 487 | 1 | no |
| `TOS` | 633 | 1 | no |
| `TOY` | 488 | 1 | no |
| `TRW` | 491 | 1 | no |
| `TSG` | 657 | 1 | no |
| `TT` | 737 | 2 | no |
| `TUP` | 541 | 1 | no |
| `TX` | 493 | 1 | no |
| `UAWGQ` | 496 | 1 | no |
| `UCL` | 498 | 1 | no |
| `UN` | 503 | 1 | no |
| `UPC` | 603 | 1 | no |
| `UST` | 509 | 1 | no |
| `VEEV` | 1204 | 1 | no |
| `VRT` | 1202 | 1 | no |
| `VRTS` | 659 | 1 | no |
| `VTSS` | 702 | 1 | no |
| `WB` | 516 | 1 | no |
| `WCOEQ` | 539 | 1 | no |
| `WLL` | 521 | 1 | no |
| `WLP` | 623 | 1 | no |
| `WNDXQ` | 524 | 1 | no |
| `WOR` | 525 | 1 | no |
| `WYND` | 807 | 1 | no |

## What this settles and what it does not

- A recommended exclusion removes a FICTIONAL series from the roster, not a real loser: the companies these tickers belonged to have their true history simply absent on this data tier. The survivorship caveat goes in the calibration report (D49 discipline), not silently.
- Exclusion changes NOTHING retroactively: generation-1 curves already inhaled these prints (finding 348's contamination). The clean numbers come from the generation-2 re-run on the excluded roster, never from patching stored curves.
- Fresh ingests are already protected by the v1.9.41 gate; this sweep exists for the corpus that predates it, and re-running it after any future bulk backfill is cheap and sanctioned.
