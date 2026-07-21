#include <stdio.h>
#include "ansi.h"
#include <stdint.h>
#include <ctype.h>
#include <conio.h>

#define GHOST_TIMEOUT	5
#define EDIBLE_TIMEOUT	300
#define SCATTER_TIMEOUT	800

int play_game(int seed);

int8_t ghost_x[5];
int8_t ghost_y[5];
int8_t ghost_direction[5];
int8_t ghost_start_x[5];
int8_t ghost_start_y[5];
	
int8_t player_x;
int8_t player_y;
int8_t start_x;
int8_t start_y;

int8_t colour = 1;
int8_t edible = 0;

uint32_t dots = 0;

char level_1[25][40] = {"#######################################",
					  "#   #              #              #   #",
					  "# # # ##### # #### # #### # ##### # # #",
					  "#         # #             # #         #",
					  "### ##### # ####### ####### # ##### ###",
					  "#       #                     #       #",
					  "# ##### ### ############### ### ##### #",
					  "# #                                 # #",
					  "# # ####### # ############# ####### # #",
					  "#   #       #             #       #   #",
					  "# # # # ### ### # ### ### # # # # # # #",
					  "#   # #       # #XGXGX#   #   # # #   #",
					  "# # # # ### # # #XXGXX# # # # # # # # #",
					  "#           #   #XGXGX# #             #",
					  "######### # # ######### ### # #########",
					  "#         # #             # #         #",
					  "# # ####### ############# # ####### # #",
					  "# #                                 # #",
					  "# ##### ### ############### ### ##### #",
					  "#       #          S          #       #",
					  "### ##### # ####### ####### # ##### ###",
					  "#         # #             # #         #",
					  "# # # ##### # #### # #### # ##### # # #",
					  "#   #       #      #      #       #   #",
					  "#######################################"};

char board[25][39];

void setup_level(int level) {
	uint8_t x, y;
	int g = 0;
	int i;
	
	dots = 0;

	for (x=0;x<39;x++) {
		for (y=0;y<25;y++) {
			if (level == 1) {
				board[y][x] = level_1[y][x];
			}

			if (board[y][x] == 'S') {
				player_x = x;
				player_y = y;
				start_x = x;
				start_y = y;
				board[y][x] = 'X';
			}

			if (board[y][x] == 'G') {
				ghost_start_x[g] = x;
				ghost_start_y[g] = y;
				ghost_x[g] = x;
				ghost_y[g] = y;
				ghost_direction[g] = 0;
				g++;
				board[y][x] = 'X';
			}

			if (board[y][x] == ' ') {
				board[y][x] = '.';
				dots++;
			}
			if (board[y][x] == 'X') {
				board[y][x] = ' ';
			}
		}
	}

	for (i=0;i<6;i++) {
		do {
			x = rand() % 39;
			y = rand() % 25;
		} while (board[y][x] != '.');

		board[y][x] = '+';
	}

	for (x=0;x < 39;x++) {
		for (y=0;y< 25;y++) {
			goto_xy(x+1, y+1);
			if (colour) {
				if (board[y][x] == '#') {
					printf("\033[1;34m");
				}
				if (board[y][x] == '.') {
					printf("\033[1;37m");
				}
				if (board[y][x] == '+') {
					printf("\033[1;31m");
				}
			}
			printf("%c", board[y][x]);
			if (colour) {
				printf("\033[0m");
			}
		}
	}

	return;
}

int main(int argc, char **argv) {
	int8_t done = 0;
	uint16_t seed;
	char c;
	uint32_t lscore = 0;

	while (!done) {
		clr_scr();
		if (!colour) {
			printf("   _ (`-.    ('-.                \r\n");
			printf("  ( (OO  )  ( OO ).-.            \r\n");
			printf(" _.`     \\  / . --. /   .-----.  \r\n");
			printf("(__...--''  | \\-.  \\   '  .--./  \r\n");
			printf(" |  /  | |.-'-'  |  |  |  |('-.  \r\n");
			printf(" |  |_.' | \\| |_.'  | /_) |OO  ) \r\n");
			printf(" |  .___.'  |  .-.  | ||  |`-'|  \r\n");
			printf(" |  |       |  | |  |(_'  '--'\\  \r\n");
			printf(" `--'       `--' `--'   `-----'  \r\n");
			printf("\r\n");
			printf("   LAST SCORE %lu!\n\n", lscore);
			printf("   P - Play\n");
			printf("   C - ANSI Color (off)\n");
	    	printf("   Q - Quit\n\n   > ");	
	    } else {
			printf("\033[1;37m   _ (`-.    ('-.                \r\n");
			printf("  ( (\033[1;30mOO  \033[1;37m)  ( \033[1;30mOO \033[1;37m).-.            \r\n");
			printf(" \033[1;37m_.`     \\  / . \033[1;34m--\033[1;37m. /   \033[1;34m.-----.  \r\n");
			printf("\033[1;37m(__\033[1;37m...\033[1;34m--\033[1;37m''  | \033[1;37m\\\033[1;34m-.  \\   '  .--./  \r\n");
			printf(" \033[1;34m|  /  | |\033[1;37m.-'-'\033[1;37m  \033[1;34m|  |  |  |\033[1;37m('-.  \r\n");
			printf(" \033[1;34m|  |_.' | \033[1;37m\\\033[1;34m| |_.'  | \033[1;37m/_) \033[1;34m|\033[1;30mOO  \033[1;37m) \r\n");
			printf(" \033[1;34m|  .___.'  |  .-.  | \033[1;37m|\033[1;34m|  |\033[1;37m`-'|  \r\n");
			printf(" \033[1;34m|  |       |  | |  |\033[1;37m(_\033[1;34m'  '--'\\  \r\n");
			printf(" \033[1;34m`--'       `--' `--'   `-----'  \r\n");
			printf("\r\n");
			printf("   \033[1;33mLAST SCORE %lu!\n\n", lscore);
			printf("   \033[1;36mP - \033[1;37mPlay\n");
			printf("   \033[1;36mC - \033[1;37mANSI Color (\033[1;32mon\033[1;37m)\n");
	    	printf("   \033[1;36mQ - \033[1;37mQuit\n\n   > ");
	    }

		do {
			c = getk();
			seed++;
		} while (c=='');

	    switch (tolower(c)) {
	    	case 'p':
	    		lscore = play_game(seed);
	    		break;
			case 'c':
				colour = !colour;
				break;
			case 'q':
				done = 1;
				break;	    	
	    }
	}

	if (colour) printf("\033[m");
	printf("\n\n");
}

int play_game(int seed) {
	int8_t player_dir = 1;
	int8_t done = 0;
	int8_t old_player_x = 0;
	int8_t old_player_y = 0;
	uint32_t score = 0;
	uint8_t lives = 0;
	char player_spr[5] = "V<^>";

	int8_t can_move_up;
	int8_t can_move_down;
	int8_t can_move_left;
	int8_t can_move_right;
	int8_t can_move;


	char in_c;
	uint8_t i, j;
	uint8_t ghost_timer = 0;
	clr_scr();
	uint16_t edible_timer = 0;
	uint16_t scatter_timer = SCATTER_TIMEOUT;
	srand(seed);

	lives = 5;
	setup_level(1);

	goto_xy(42, 1);
	printf("\033[1;36mP A C\033[0m");

	goto_xy(42, 6);
	printf("\033[1;31m+ \033[0mCherry, Makes Ghosts Edible");
	goto_xy(42, 8);
	printf("\033[1;35mH \033[0mA Ghost! Look out!");
	goto_xy(42, 10);
	printf("\033[1;32mM \033[0mA frightend Ghost! Eat it!");
	goto_xy(42, 12);
	printf("\033[1;33m< \033[0mYou!");
	goto_xy(42, 14);
	printf("\033[1;31mNUM LOCK ON TO PLAY!! Q to Quit");


	while (!done) {
		if (old_player_x != player_x || old_player_y != player_y) {
			goto_xy(player_x + 1, player_y + 1);
			if (colour) {
				printf("\033[1;33m");
			}
			printf("%c", player_spr[player_dir]);
			if (colour) {
				printf("\033[0m");
			}
			goto_xy(old_player_x + 1, old_player_y + 1);
			printf(" ");
			old_player_x = player_x;
			old_player_y = player_y;
		}

		if (edible_timer == 0 && edible == 1) {
			edible = 0;
		} else {
			if (edible_timer != 0) {
				edible_timer--;
			}
		}

		if (ghost_timer == 0) {
			for (i=0;i<5;i++) {
				goto_xy(ghost_x[i] + 1, ghost_y[i] + 1);
				if (colour) {
					if (board[ghost_y[i]][ghost_x[i]] == '.') {
						printf("\033[1;37m");
					} else if (board[ghost_y[i]][ghost_x[i]] == '+') {
						printf("\033[1;31m");
					}
				}
				printf("%c", board[ghost_y[i]][ghost_x[i]]);

				// move ghost

				can_move_up = 0;
				can_move_down = 0;
				can_move_left = 0;
				can_move_right = 0;

				if (ghost_x[i] > 0 && board[ghost_y[i]][ghost_x[i] - 1] != '#') {
					can_move_left = 1;
				} 
				if (ghost_y[i] > 0 && board[ghost_y[i] - 1][ghost_x[i]] != '#') {
					can_move_up = 1;
				} 
				if (ghost_x[i] < 39 && board[ghost_y[i]][ghost_x[i] + 1] != '#') {
					can_move_right = 1;
				} 
				if (ghost_y[i] < 25 && board[ghost_y[i] + 1][ghost_x[i]] != '#') {
					can_move_down = 1;
				}

				for (j=0;j<5;j++) {
					if (ghost_x[j] == ghost_x[i] - 1 && ghost_y[j] == ghost_y[i]) can_move_left = 0;
					if (ghost_x[j] == ghost_x[i] + 1 && ghost_y[j] == ghost_y[i]) can_move_right = 0;
					if (ghost_y[j] == ghost_y[i] - 1 && ghost_x[j] == ghost_x[i]) can_move_up = 0;
					if (ghost_y[j] == ghost_y[i] + 1 && ghost_x[j] == ghost_x[i]) can_move_down = 0;
				}

				can_move = can_move_left + can_move_right + can_move_up + can_move_down;

				if (can_move) {
					if (scatter_timer > 0) {
						scatter_timer--;
						if ((ghost_direction[i] == 1 && !can_move_up) ||
							(ghost_direction[i] == 2 && !can_move_right) ||
							(ghost_direction[i] == 3 && !can_move_down) ||
							(ghost_direction[i] == 4 && !can_move_left) ||
							ghost_direction[i] == 0) {
							switch(rand() % can_move + 1) {
								case 1:
									if (can_move_left) {
										ghost_x[i]--;
										ghost_direction[i] = 4;
									} else if (can_move_right) {
										ghost_x[i]++;
										ghost_direction[i] = 2;
									} else if (can_move_up) {
										ghost_y[i]--;
										ghost_direction[i] = 1;
									} else {
										ghost_y[i]++;
										ghost_direction[i] = 3;
									}
									break;
								case 2:
									if (can_move_right) {
										ghost_x[i]++;
										ghost_direction[i] = 2;
									} else if (can_move_up) {
										ghost_y[i]--;
										ghost_direction[i] = 1;
									} else if (can_move_down) {
										ghost_y[i]++;
										ghost_direction[i] = 3;
									} else {
										ghost_x[i]--;
										ghost_direction[i] = 4;
									}
									break;							
								case 3:
									if (can_move_up) {
										ghost_y[i]--;
										ghost_direction[i] = 1;
									} else if (can_move_down) {
										ghost_y[i]++;
										ghost_direction[i] = 3;
									} else if (can_move_left) {
										ghost_x[i]--;
										ghost_direction[i] = 4;
									} else {
										ghost_x[i]++;
										ghost_direction[i] = 2;
									}
									break;
								case 4:
									if (can_move_down) {
										ghost_y[i]++;
										ghost_direction[i] = 3;
									} else if (can_move_left) {
										ghost_x[i]--;
										ghost_direction[i] = 4;
									} else if (can_move_right) {
										ghost_x[i]++;
										ghost_direction[i] = 2;
									} else {
										ghost_y[i]--;
										ghost_direction[i] = 1;
									}
									break;
							}
						} else {
							if (ghost_direction[i] == 1) {
								ghost_y[i]--;
							} else if (ghost_direction[i] == 2) {
								ghost_x[i]++;
							} else if (ghost_direction[i] == 3) {
								ghost_y[i]++;
						    } else if (ghost_direction[i] == 4) {
						    	ghost_x[i]--;
							}
						}
					} else {
						// todo Chase player...
						if (!edible) {
							if ((ghost_direction[i] == 1 && !can_move_up) ||
								(ghost_direction[i] == 2 && !can_move_right) ||
								(ghost_direction[i] == 3 && !can_move_down) ||
								(ghost_direction[i] == 4 && !can_move_left) ||
								ghost_direction[i] == 0) {
								
								if (player_x < ghost_x[i] && can_move_left) {
									ghost_direction[i] = 4;
									ghost_x[i]--;
								} else if (player_x > ghost_x[i] && can_move_right) {
									ghost_direction[i] = 2;
									ghost_x[i]++;
								} else if (player_y < ghost_y[i] && can_move_up) {
									ghost_direction[i] = 1;
									ghost_y[i]--;
								} else if (player_y > ghost_y[i] && can_move_down) {
									ghost_direction[i] = 3;
									ghost_y[i]++;
								} else {
									switch(rand() % can_move + 1) {
										case 1:
											if (can_move_left) {
												ghost_x[i]--;
												ghost_direction[i] = 4;
											} else if (can_move_right) {
												ghost_x[i]++;
												ghost_direction[i] = 2;
											} else if (can_move_up) {
												ghost_y[i]--;
												ghost_direction[i] = 1;
											} else {
												ghost_y[i]++;
												ghost_direction[i] = 3;
											}
											break;
										case 2:
											if (can_move_right) {
												ghost_x[i]++;
												ghost_direction[i] = 2;
											} else if (can_move_up) {
												ghost_y[i]--;
												ghost_direction[i] = 1;
											} else if (can_move_down) {
												ghost_y[i]++;
												ghost_direction[i] = 3;
											} else {
												ghost_x[i]--;
												ghost_direction[i] = 4;
											}
											break;							
										case 3:
											if (can_move_up) {
												ghost_y[i]--;
												ghost_direction[i] = 1;
											} else if (can_move_down) {
												ghost_y[i]++;
												ghost_direction[i] = 3;
											} else if (can_move_left) {
												ghost_x[i]--;
												ghost_direction[i] = 4;
											} else {
												ghost_x[i]++;
												ghost_direction[i] = 2;
											}
											break;
										case 4:
											if (can_move_down) {
												ghost_y[i]++;
												ghost_direction[i] = 3;
											} else if (can_move_left) {
												ghost_x[i]--;
												ghost_direction[i] = 4;
											} else if (can_move_right) {
												ghost_x[i]++;
												ghost_direction[i] = 2;
											} else {
												ghost_y[i]--;
												ghost_direction[i] = 1;
											}
											break;
									}
								}
							} else {
								if (ghost_direction[i] == 1) {
									ghost_y[i]--;
								} else if (ghost_direction[i] == 2) {
									ghost_x[i]++;
								} else if (ghost_direction[i] == 3) {
									ghost_y[i]++;
							    } else if (ghost_direction[i] == 4) {
							    	ghost_x[i]--;
								}
							}
						} else {
							if ((ghost_direction[i] == 1 && !can_move_up) ||
								(ghost_direction[i] == 2 && !can_move_right) ||
								(ghost_direction[i] == 3 && !can_move_down) ||
								(ghost_direction[i] == 4 && !can_move_left) ||
								ghost_direction[i] == 0) {
								
								if (player_x < ghost_x[i] && can_move_right) {
									ghost_direction[i] = 2;
									ghost_x[i]++;
								} else if (player_x > ghost_x[i] && can_move_left) {
									ghost_direction[i] = 4;
									ghost_x[i]--;
								} else if (player_y < ghost_y[i] && can_move_down) {
									ghost_direction[i] = 3;
									ghost_y[i]++;
								} else if (player_y > ghost_y[i] && can_move_up) {
									ghost_direction[i] = 1;
									ghost_y[i]--;
								} else {
									switch(rand() % can_move + 1) {
										case 1:
											if (can_move_left) {
												ghost_x[i]--;
												ghost_direction[i] = 4;
											} else if (can_move_right) {
												ghost_x[i]++;
												ghost_direction[i] = 2;
											} else if (can_move_up) {
												ghost_y[i]--;
												ghost_direction[i] = 1;
											} else {
												ghost_y[i]++;
												ghost_direction[i] = 3;
											}
											break;
										case 2:
											if (can_move_right) {
												ghost_x[i]++;
												ghost_direction[i] = 2;
											} else if (can_move_up) {
												ghost_y[i]--;
												ghost_direction[i] = 1;
											} else if (can_move_down) {
												ghost_y[i]++;
												ghost_direction[i] = 3;
											} else {
												ghost_x[i]--;
												ghost_direction[i] = 4;
											}
											break;							
										case 3:
											if (can_move_up) {
												ghost_y[i]--;
												ghost_direction[i] = 1;
											} else if (can_move_down) {
												ghost_y[i]++;
												ghost_direction[i] = 3;
											} else if (can_move_left) {
												ghost_x[i]--;
												ghost_direction[i] = 4;
											} else {
												ghost_x[i]++;
												ghost_direction[i] = 2;
											}
											break;
										case 4:
											if (can_move_down) {
												ghost_y[i]++;
												ghost_direction[i] = 3;
											} else if (can_move_left) {
												ghost_x[i]--;
												ghost_direction[i] = 4;
											} else if (can_move_right) {
												ghost_x[i]++;
												ghost_direction[i] = 2;
											} else {
												ghost_y[i]--;
												ghost_direction[i] = 1;
											}
											break;
									}
								}
							} else {
								if (ghost_direction[i] == 1) {
									ghost_y[i]--;
								} else if (ghost_direction[i] == 2) {
									ghost_x[i]++;
								} else if (ghost_direction[i] == 3) {
									ghost_y[i]++;
							    } else if (ghost_direction[i] == 4) {
							    	ghost_x[i]--;
								}
							}							
						}
					}
				}

				goto_xy(ghost_x[i] + 1, ghost_y[i] + 1);
				if (ghost_x[i] == player_x && ghost_y[i] == player_y) {
					if (edible) {
						score += 100;
						ghost_x[i] = ghost_start_x[i];
						ghost_y[i] = ghost_start_y[i];
					} else {
						lives--;
						if (lives == 0) {
							clr_scr();

							goto_xy(30, 12);
							printf("You LOSE!, SCORE: %lu", score);
							goto_xy(30, 14);
							printf("Press a key...");
							getchar();
							return score;
						}

						player_x = start_x;
						player_y = start_y;
					}
				}
				if (edible) {
					if (colour) {
						printf("\033[1;32m");
					}
					printf("M");
				} else {
					if (colour) {
						printf("\033[1;35m");
					}					
					printf("H");
				}
				if (colour) {
					printf("\033[0m");
				}
			}
			ghost_timer = GHOST_TIMEOUT;
		} else {
			ghost_timer--;
		}
		goto_xy(42, 3);
		printf("SCORE: %lu", score);
		goto_xy(42, 4);
		printf("LIVES: %d", lives);
		if ((in_c = getk()) != 0) {
			switch (in_c) {
				case 'w':
				case 'W':
				case '8':
					if (player_y - 1 >= 0 && board[player_y - 1][player_x] != '#') {
						player_y--;
					}
					player_dir = 0;
					break;
				case 'd':
				case 'D':
				case '6':
					if (player_x + 1 <= 39 && board[player_y][player_x + 1] != '#') {
						player_x++;
					}
					player_dir = 1;
					break;
				case 's':
				case 'S':
				case '2':
					if (player_y + 1 <= 25 && board[player_y + 1][player_x] != '#') {
						player_y++;
					}
					player_dir = 2;
					break;
				case 'a':
				case 'A':
				case '4':
					if (player_x - 1 >= 0 && board[player_y][player_x - 1] != '#') {
						player_x--;
					}
					player_dir = 3;
					break;
				case 'q':
				case 'Q':
					done = 1;
					break;
			}
			if (board[player_y][player_x] == '.') {
				board[player_y][player_x] = ' ';
				score++;
				dots--;
				if (dots == 0) {
					clr_scr();

					score += (lives * 500);
					goto_xy(30, 12);
					printf("You WIN! SCORE: %lu\r\n", score);
					goto_xy(30, 14);
					printf("Press a key...");
					getchar();
					return score;
				}
			} else if (board[player_y][player_x] == '+') {
				board[player_y][player_x] = ' ';
				edible = 1;
				edible_timer = EDIBLE_TIMEOUT;
				dots--;
				if (dots == 0) {
					clr_scr();

					score += (lives * 500);
					goto_xy(30, 12);
					printf("You WIN! SCORE: %lu\r\n", score);
					goto_xy(30, 14);
					printf("Press a key...");
					getchar();
					return score;
				}				
			}
			for (i=0;i<5;i++) {
				if (ghost_x[i] == player_x && ghost_y[i] == player_y) {
					if (edible) {
						score += 100;
						ghost_x[i] = ghost_start_x[i];
						ghost_y[i] = ghost_start_y[i];
					} else {
						lives--;
						if (lives == 0) {
							clr_scr();

							goto_xy(30, 12);
							printf("You LOSE!, SCORE: %lu", score);
							goto_xy(30, 14);
							printf("Press a key...");
							getchar();
							return score;
						}

						player_x = start_x;
						player_y = start_y;
					}
				}			
			}
		}
	}

	return 0;
}
