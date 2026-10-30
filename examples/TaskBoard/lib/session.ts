import { useSharedProps } from 'netjes';
import { type SignedInUser, sharedProps } from '../generated/netjes.ts';

/** The signed-in user (SharedProps in Program.cs, from the auth cookie), on every page. */
export function useUser(): SignedInUser | null {
  return useSharedProps(sharedProps).user;
}
